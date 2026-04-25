using Microsoft.Data.Sqlite;
using FluentAssertions;
using Cabal.Scheduler.Core;
using Cabal.SQLite;

namespace Cabal.Tests;

/// <summary>
/// Integration tests against an in-memory SQLite database.
/// Each test gets an isolated DB via a unique named shared-cache connection.
/// </summary>
public class ScheduleStaticsStateTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly SqliteJobStorage _storage;

    public ScheduleStaticsStateTests()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connStr);
        _keepAlive.Open();
        _storage = new SqliteJobStorage(connStr);
    }

    public void Dispose() => _keepAlive.Dispose();

    /// <summary>
    /// When a job's interval is changed in code and the app restarts,
    /// SyncJobsFromMemoryAsync should update the stored interval.
    /// </summary>
    [Fact]
    public async Task Sync_WhenIntervalChanged_ShouldUpdateIntervalInDatabase()
    {
        await _storage.InitializeDatabaseAsync();

        var original = new JobDefinition { Name = "Email Sender", Interval = TimeSpan.FromSeconds(60) };
        await _storage.SyncJobsFromMemoryAsync([original]);

        var updated = new JobDefinition { Name = "Email Sender", Interval = TimeSpan.FromSeconds(120) };
        await _storage.SyncJobsFromMemoryAsync([updated]);

        var stats = await _storage.GetDashboardStatsAsync();
        var jobInDb = stats.Jobs.Single(j => j.Name == "Email Sender");

        jobInDb.IntervalSeconds.Should().Be(120,
            because: "the scheduler must use the current code interval, not the one from first boot");
    }

    /// <summary>
    /// When a job is removed from code, it should be deleted from the database
    /// on the next sync so it doesn't keep running as a ghost.
    /// </summary>
    [Fact]
    public async Task Sync_WhenJobRemovedFromCode_ShouldDeleteItFromDatabase()
    {
        await _storage.InitializeDatabaseAsync();

        var jobA = new JobDefinition { Name = "Job A", Interval = TimeSpan.FromSeconds(60) };
        var jobB = new JobDefinition { Name = "Job B", Interval = TimeSpan.FromSeconds(60) };
        await _storage.SyncJobsFromMemoryAsync([jobA, jobB]);

        await _storage.SyncJobsFromMemoryAsync([jobA]);

        var stats = await _storage.GetDashboardStatsAsync();

        stats.Jobs.Should().ContainSingle(j => j.Name == "Job A");
        stats.Jobs.Should().NotContain(j => j.Name == "Job B",
            because: "a job removed from code must not keep running from the database");
    }

    /// <summary>
    /// Happy path: completing a job should update NextExecution AND
    /// create exactly one history entry — both atomically.
    /// </summary>
    [Fact]
    public async Task MarkCompleted_HappyPath_ShouldUpdateJobAndWriteHistoryTogether()
    {
        await _storage.InitializeDatabaseAsync();

        var job = new JobDefinition { Name = "Ping", Interval = TimeSpan.FromSeconds(30) };
        await _storage.SyncJobsFromMemoryAsync([job]);

        var jobId = await _storage.GetAndLockNextJobAsync(DateTime.UtcNow.AddSeconds(31));
        jobId.Should().NotBeNull();

        await _storage.MarkJobAsCompletedAsync(jobId!, intervalSeconds: 30, success: true, errorMessage: null);

        var stats = await _storage.GetDashboardStatsAsync();

        stats.TotalExecutions.Should().Be(1, because: "there should be exactly one history entry");
        stats.FailedExecutions.Should().Be(0);
        stats.Jobs.Single(j => j.Name == "Ping").NextExecution
            .Should().NotBeNullOrEmpty(because: "NextExecution must have been recalculated");
    }

    /// <summary>
    /// Calling MarkJobAsCompletedAsync with an unknown jobId must not create
    /// orphan history entries. The UPDATE and INSERT must be treated atomically.
    /// Currently passes (the INSERT subquery returns no rows), but documents
    /// the expected contract and guards against future regressions.
    /// </summary>
    [Fact]
    public async Task MarkCompleted_WhenJobDoesNotExist_ShouldNotCreateOrphanHistory()
    {
        await _storage.InitializeDatabaseAsync();

        var fakeId = Guid.NewGuid().ToString("N");
        await _storage.MarkJobAsCompletedAsync(fakeId, intervalSeconds: 60, success: true, errorMessage: null);

        var stats = await _storage.GetDashboardStatsAsync();

        stats.History.Should().BeEmpty(
            because: "there should be no history for a job that never existed in the database");
        stats.TotalExecutions.Should().Be(0);
    }
}