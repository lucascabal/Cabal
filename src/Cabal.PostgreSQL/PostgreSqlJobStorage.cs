using Npgsql;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;

namespace Cabal.PostgreSQL;

public class PostgreSqlJobStorage : IJobStorage
{
    private readonly string _connectionString;

    public PostgreSqlJobStorage(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ScheduledJobs (
                Id TEXT PRIMARY KEY,
                Name TEXT UNIQUE NOT NULL,
                IntervalSeconds INTEGER NOT NULL,
                LockTimeoutSeconds INTEGER NOT NULL DEFAULT 300,
                LastExecution TIMESTAMP NULL,
                NextExecution TIMESTAMP NOT NULL,
                LockedUntil TIMESTAMP NULL
            );

            CREATE TABLE IF NOT EXISTS JobHistory (
                Id SERIAL PRIMARY KEY,
                JobId TEXT NOT NULL,
                JobName TEXT NOT NULL,
                ExecutedAt TIMESTAMP NOT NULL,
                Status TEXT NOT NULL,
                ErrorMessage TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_Next_Locked ON ScheduledJobs(NextExecution, LockedUntil);
            CREATE INDEX IF NOT EXISTS IX_JobHistory_ExecutedAt ON JobHistory(ExecutedAt);
        ";
        await command.ExecuteNonQueryAsync();

        await using var migrateCmd = connection.CreateCommand();
        migrateCmd.CommandText = @"
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = 'scheduledjobs' AND column_name = 'locktimeoutseconds';
        ";
        var columnExists = Convert.ToInt32(await migrateCmd.ExecuteScalarAsync()) > 0;

        if (!columnExists)
        {
            await using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN LockTimeoutSeconds INTEGER NOT NULL DEFAULT 300;";
            await alterCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task SyncJobsFromMemoryAsync(IEnumerable<JobDefinition> jobs)
    {
        var jobList = jobs.ToList();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        foreach (var job in jobList)
        {
            await using var upsertCmd = connection.CreateCommand();
            upsertCmd.CommandText = @"
                INSERT INTO ScheduledJobs (Id, Name, IntervalSeconds, LockTimeoutSeconds, NextExecution)
                VALUES (@id, @name, @interval, @lockTimeout, @nextExecution)
                ON CONFLICT (Name) DO UPDATE SET
                    IntervalSeconds = EXCLUDED.IntervalSeconds,
                    LockTimeoutSeconds = EXCLUDED.LockTimeoutSeconds;
            ";
            upsertCmd.Parameters.AddWithValue("id", job.Id);
            upsertCmd.Parameters.AddWithValue("name", job.Name);
            upsertCmd.Parameters.AddWithValue("interval", (int)job.Interval.TotalSeconds);
            upsertCmd.Parameters.AddWithValue("lockTimeout", (int)job.LockTimeout.TotalSeconds);
            upsertCmd.Parameters.AddWithValue("nextExecution", DateTime.UtcNow.Add(job.Interval));
            await upsertCmd.ExecuteNonQueryAsync();
        }

        if (jobList.Count > 0)
        {
            var placeholders = string.Join(", ", jobList.Select((_, i) => $"@name{i}"));
            await using var deleteCmd = connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM ScheduledJobs WHERE Name NOT IN ({placeholders});";
            for (int i = 0; i < jobList.Count; i++)
                deleteCmd.Parameters.AddWithValue($"name{i}", jobList[i].Name);
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
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = @"
            UPDATE ScheduledJobs
            SET LockedUntil = @now + (LockTimeoutSeconds * INTERVAL '1 second')
            WHERE Id = (
                SELECT Id FROM ScheduledJobs
                WHERE NextExecution <= @now
                  AND (LockedUntil IS NULL OR LockedUntil < @now)
                ORDER BY NextExecution ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING Id;
        ";

        command.Parameters.AddWithValue("now", now);

        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task<JobDefinitionRecord?> GetJobByIdAsync(string id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, IntervalSeconds FROM ScheduledJobs WHERE Id = @id LIMIT 1;";
        command.Parameters.AddWithValue("id", id);

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
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE ScheduledJobs
            SET LastExecution = @now, NextExecution = @next, LockedUntil = NULL
            WHERE Id = @id;
        ";
        updateCmd.Parameters.AddWithValue("id", jobId);
        updateCmd.Parameters.AddWithValue("now", now);
        updateCmd.Parameters.AddWithValue("next", now.AddSeconds(intervalSeconds));
        var rowsAffected = await updateCmd.ExecuteNonQueryAsync();

        if (rowsAffected > 0)
        {
            await using var historyCmd = connection.CreateCommand();
            historyCmd.CommandText = @"
                INSERT INTO JobHistory (JobId, JobName, ExecutedAt, Status, ErrorMessage)
                SELECT @id, Name, @now, @status, @error
                FROM ScheduledJobs WHERE Id = @id;
            ";
            historyCmd.Parameters.AddWithValue("id", jobId);
            historyCmd.Parameters.AddWithValue("now", now);
            historyCmd.Parameters.AddWithValue("status", success ? "Success" : "Error");
            historyCmd.Parameters.AddWithValue("error", errorMessage ?? (object)DBNull.Value);
            await historyCmd.ExecuteNonQueryAsync();
        }

        await using var cleanupCmd = connection.CreateCommand();
        cleanupCmd.CommandText = "DELETE FROM JobHistory WHERE ExecutedAt < @cutoff;";
        cleanupCmd.Parameters.AddWithValue("cutoff", now.AddDays(-7));
        await cleanupCmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    public async Task<DashboardStats> GetDashboardStatsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
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
                NextExecution: readerJobs.GetDateTime(2).ToString("O"),
                LockedUntil: readerJobs.IsDBNull(3) ? null : readerJobs.GetDateTime(3).ToString("O")
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
                ExecutedAt: readerHistory.GetDateTime(1).ToString("O"),
                Status: readerHistory.GetString(2),
                ErrorMessage: readerHistory.IsDBNull(3) ? null : readerHistory.GetString(3)
            ));
        }

        var performanceGraph = new List<GraphPoint>();
        await using var cmdGraph = connection.CreateCommand();
        cmdGraph.CommandText = @"
            SELECT
                (CAST(EXTRACT(EPOCH FROM ExecutedAt) AS BIGINT) / 60) * 60 AS Timestamp,
                COUNT(*)::INTEGER AS Executions
            FROM JobHistory
            WHERE Status = 'Success'
              AND ExecutedAt >= NOW() - INTERVAL '1 hour'
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
