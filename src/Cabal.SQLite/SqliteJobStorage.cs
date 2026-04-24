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

    public void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();

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
        command.ExecuteNonQuery();
    }

    public void SyncJobsFromMemory(IEnumerable<JobDefinition> jobs)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        
        foreach (var job in jobs)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR IGNORE INTO ScheduledJobs (Id, Name, IntervalSeconds, NextExecution)
                VALUES (@id, @name, @interval, @nextExecution);
            ";
            command.Parameters.AddWithValue("@id", job.Id);
            command.Parameters.AddWithValue("@name", job.Name);
            command.Parameters.AddWithValue("@interval", job.Interval.TotalSeconds);
            command.Parameters.AddWithValue("@nextExecution", DateTime.UtcNow.Add(job.Interval).ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string? GetAndLockNextJob(DateTime now)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        
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
        command.Parameters.AddWithValue("@lockedUntil", now.AddMinutes(5).ToString("O")); // Lock de seguridad

        var result = command.ExecuteScalar();
        return result?.ToString();
    }

    public JobDefinitionRecord? GetJobById(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM ScheduledJobs WHERE Id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);
        
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new JobDefinitionRecord(reader.GetString(0), reader.GetString(1));
        }
        return null;
    }

    public void MarkJobAsCompleted(string jobId, int intervalSeconds, bool success, string? errorMessage)
    {
        var now = DateTime.UtcNow;
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        
        command.CommandText = @"
            UPDATE ScheduledJobs 
            SET LastExecution = @now, NextExecution = @next, LockedUntil = NULL 
            WHERE Id = @id;

            INSERT INTO JobHistory (JobId, JobName, ExecutedAt, Status, ErrorMessage)
            SELECT @id, Name, @now, @status, @error
            FROM ScheduledJobs WHERE Id = @id;
        ";

        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@now", now.ToString("O"));
        command.Parameters.AddWithValue("@next", now.AddSeconds(intervalSeconds).ToString("O"));
        command.Parameters.AddWithValue("@status", success ? "Success" : "Error");
        command.Parameters.AddWithValue("@error", errorMessage ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public DashboardStats GetDashboardStats()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmdActive = connection.CreateCommand();
        cmdActive.CommandText = "SELECT COUNT(*) FROM ScheduledJobs;";
        var activeJobs = Convert.ToInt32(cmdActive.ExecuteScalar());

        using var cmdTotal = connection.CreateCommand();
        cmdTotal.CommandText = "SELECT COUNT(*) FROM JobHistory;";
        var totalExecutions = Convert.ToInt32(cmdTotal.ExecuteScalar());

        using var cmdFailed = connection.CreateCommand();
        cmdFailed.CommandText = "SELECT COUNT(*) FROM JobHistory WHERE Status = 'Error';";
        var failedExecutions = Convert.ToInt32(cmdFailed.ExecuteScalar());

        var jobs = new List<JobInfo>();
        using var cmdJobs = connection.CreateCommand();
        cmdJobs.CommandText = "SELECT Name, IntervalSeconds, NextExecution, LockedUntil FROM ScheduledJobs ORDER BY Name;";
        using var readerJobs = cmdJobs.ExecuteReader();
        while (readerJobs.Read())
        {
            jobs.Add(new JobInfo(
                Name: readerJobs.GetString(0),
                IntervalSeconds: readerJobs.GetInt32(1),
                NextExecution: readerJobs.GetString(2),
                LockedUntil: readerJobs.IsDBNull(3) ? null : readerJobs.GetString(3)
            ));
        }

        var history = new List<JobHistoryLog>();
        using var cmdHistory = connection.CreateCommand();
        cmdHistory.CommandText = "SELECT JobName, ExecutedAt, Status, ErrorMessage FROM JobHistory ORDER BY Id DESC LIMIT 10;";
        using var readerHistory = cmdHistory.ExecuteReader();
        while (readerHistory.Read())
        {
            history.Add(new JobHistoryLog(
                JobName: readerHistory.GetString(0),
                ExecutedAt: readerHistory.GetString(1),
                Status: readerHistory.GetString(2),
                ErrorMessage: readerHistory.IsDBNull(3) ? null : readerHistory.GetString(3)
            ));
        }

        var performanceGraph = new List<GraphPoint>();
        using var cmdGraph = connection.CreateCommand();
        cmdGraph.CommandText = @"
            SELECT
                (CAST(strftime('%s', ExecutedAt) AS INTEGER) / 60) * 60 AS Timestamp,
                COUNT(*) AS Executions
            FROM JobHistory
            WHERE Status = 'Success'
              AND ExecutedAt >= datetime('now', '-1 hour')
            GROUP BY Timestamp
            ORDER BY Timestamp ASC;";
            
        using var readerGraph = cmdGraph.ExecuteReader();
        while (readerGraph.Read())
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