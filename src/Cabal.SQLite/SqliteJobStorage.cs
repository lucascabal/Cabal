using Microsoft.Data.Sqlite;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;

namespace Cabal.SQLite;

public class SqliteJobStorage : IJobStorage
{
    private readonly string _connectionString;

    public SqliteJobStorage(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeDatabaseAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync();

        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ScheduledJobs (
                Id TEXT PRIMARY KEY,
                Name TEXT UNIQUE NOT NULL,
                IntervalSeconds INTEGER NOT NULL,
                LastExecution TEXT NULL,
                NextExecution TEXT NOT NULL,
                LockedUntil TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS JobHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId TEXT NOT NULL,
                JobName TEXT NOT NULL,
                ExecutedAt TEXT NOT NULL,
                Status TEXT NOT NULL,
                ErrorMessage TEXT NULL
            );
        ";
        await command.ExecuteNonQueryAsync();
    }

    public async Task SyncJobsFromMemoryAsync(IEnumerable<JobDefinition> jobs)
    {
        var jobList = jobs.ToList();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var job in jobList)
        {
            await using var upsertCmd = connection.CreateCommand();
            upsertCmd.CommandText = @"
                INSERT INTO ScheduledJobs (Id, Name, IntervalSeconds, NextExecution)
                VALUES (@id, @name, @interval, @nextExecution)
                ON CONFLICT(Name) DO UPDATE SET
                    IntervalSeconds = excluded.IntervalSeconds;
            ";
            upsertCmd.Parameters.AddWithValue("@id", job.Id);
            upsertCmd.Parameters.AddWithValue("@name", job.Name);
            upsertCmd.Parameters.AddWithValue("@interval", (int)job.Interval.TotalSeconds);
            upsertCmd.Parameters.AddWithValue("@nextExecution", DateTime.UtcNow.Add(job.Interval).ToString("O"));
            await upsertCmd.ExecuteNonQueryAsync();
        }

        if (jobList.Count > 0)
        {
            var placeholders = string.Join(", ", jobList.Select((_, i) => $"@name{i}"));
            await using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM ScheduledJobs WHERE Name NOT IN ({placeholders});";
            for (int i = 0; i < jobList.Count; i++)
                deleteCmd.Parameters.AddWithValue($"@name{i}", jobList[i].Name);
            await deleteCmd.ExecuteNonQueryAsync();
        }
        else
        {
            await using var deleteAllCmd = connection.CreateCommand();
            deleteAllCmd.CommandText = "DELETE FROM ScheduledJobs;";
            await deleteAllCmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<string?> GetAndLockNextJobAsync(DateTime now)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = @"
            UPDATE ScheduledJobs
            SET LockedUntil = @lockedUntil
            WHERE Id = (
                SELECT Id FROM ScheduledJobs
                WHERE NextExecution <= @now
                  AND (LockedUntil IS NULL OR LockedUntil < @now)
                LIMIT 1
            )
            RETURNING Id;
        ";

        command.Parameters.AddWithValue("@now", now.ToString("O"));
        command.Parameters.AddWithValue("@lockedUntil", now.AddMinutes(5).ToString("O"));

        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task<JobDefinitionRecord?> GetJobByIdAsync(string id)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, IntervalSeconds FROM ScheduledJobs WHERE Id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new JobDefinitionRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
        }
        return null;
    }

    public async Task MarkJobAsCompletedAsync(string jobId, int intervalSeconds, bool success, string? errorMessage)
    {
        var now = DateTime.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE ScheduledJobs
            SET LastExecution = @now, NextExecution = @next, LockedUntil = NULL
            WHERE Id = @id;
        ";
        updateCmd.Parameters.AddWithValue("@id", jobId);
        updateCmd.Parameters.AddWithValue("@now", now.ToString("O"));
        updateCmd.Parameters.AddWithValue("@next", now.AddSeconds(intervalSeconds).ToString("O"));
        var rowsAffected = await updateCmd.ExecuteNonQueryAsync();

        if (rowsAffected > 0)
        {
            await using var historyCmd = connection.CreateCommand();
            historyCmd.CommandText = @"
                INSERT INTO JobHistory (JobId, JobName, ExecutedAt, Status, ErrorMessage)
                SELECT @id, Name, @now, @status, @error
                FROM ScheduledJobs WHERE Id = @id;
            ";
            historyCmd.Parameters.AddWithValue("@id", jobId);
            historyCmd.Parameters.AddWithValue("@now", now.ToString("O"));
            historyCmd.Parameters.AddWithValue("@status", success ? "Success" : "Error");
            historyCmd.Parameters.AddWithValue("@error", errorMessage ?? (object)DBNull.Value);
            await historyCmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<DashboardStats> GetDashboardStatsAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var cmdActive = connection.CreateCommand();
        cmdActive.CommandText = "SELECT COUNT(*) FROM ScheduledJobs;";
        var activeJobs = Convert.ToInt32(await cmdActive.ExecuteScalarAsync());

        await using var cmdTotal = connection.CreateCommand();
        cmdTotal.CommandText = "SELECT COUNT(*) FROM JobHistory;";
        var totalExecutions = Convert.ToInt32(await cmdTotal.ExecuteScalarAsync());

        await using var cmdFailed = connection.CreateCommand();
        cmdFailed.CommandText = "SELECT COUNT(*) FROM JobHistory WHERE Status = 'Error';";
        var failedExecutions = Convert.ToInt32(await cmdFailed.ExecuteScalarAsync());

        var jobs = new List<JobInfo>();
        await using var cmdJobs = connection.CreateCommand();
        cmdJobs.CommandText = "SELECT Name, IntervalSeconds, NextExecution, LockedUntil FROM ScheduledJobs ORDER BY Name;";
        await using var readerJobs = await cmdJobs.ExecuteReaderAsync();
        while (await readerJobs.ReadAsync())
        {
            jobs.Add(new JobInfo(
                Name: readerJobs.GetString(0),
                IntervalSeconds: readerJobs.GetInt32(1),
                NextExecution: readerJobs.GetString(2),
                LockedUntil: readerJobs.IsDBNull(3) ? null : readerJobs.GetString(3)
            ));
        }

        var history = new List<JobHistoryLog>();
        await using var cmdHistory = connection.CreateCommand();
        cmdHistory.CommandText = "SELECT JobName, ExecutedAt, Status, ErrorMessage FROM JobHistory ORDER BY Id DESC LIMIT 10;";
        await using var readerHistory = await cmdHistory.ExecuteReaderAsync();
        while (await readerHistory.ReadAsync())
        {
            history.Add(new JobHistoryLog(
                JobName: readerHistory.GetString(0),
                ExecutedAt: readerHistory.GetString(1),
                Status: readerHistory.GetString(2),
                ErrorMessage: readerHistory.IsDBNull(3) ? null : readerHistory.GetString(3)
            ));
        }

        var performanceGraph = new List<GraphPoint>();
        await using var cmdGraph = connection.CreateCommand();
        cmdGraph.CommandText = @"
            SELECT
                (CAST(strftime('%s', ExecutedAt) AS INTEGER) / 60) * 60 AS Timestamp,
                COUNT(*) AS Executions
            FROM JobHistory
            WHERE Status = 'Success'
              AND ExecutedAt >= datetime('now', '-1 hour')
            GROUP BY Timestamp
            ORDER BY Timestamp ASC;";

        await using var readerGraph = await cmdGraph.ExecuteReaderAsync();
        while (await readerGraph.ReadAsync())
        {
            if (!readerGraph.IsDBNull(0))
            {
                performanceGraph.Add(new GraphPoint(
                    Timestamp: readerGraph.GetInt64(0),
                    Executions: readerGraph.GetInt32(1)
                ));
            }
        }

        return new DashboardStats(
            ActiveJobs: activeJobs,
            Uptime: "Online",
            TotalExecutions: totalExecutions,
            FailedExecutions: failedExecutions,
            Jobs: jobs,
            History: history,
            PerformanceGraph: performanceGraph
        );
    }
}