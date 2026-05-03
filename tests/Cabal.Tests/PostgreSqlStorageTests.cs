using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Builders;
using Cabal.Scheduler.Core;
using AwesomeAssertions;
using Cabal.PostgreSQL;

namespace Cabal.Tests;

/// <summary>
/// Integration tests against a PostgreSQL Testcontainer.
/// Each test gets an isolated database via a dedicated container.
/// </summary>
public class PostgreSqlStorageTests : IAsyncLifetime
{
    private readonly IContainer _postgresContainer;
    private PostgreSqlJobStorage? _storage;

    public PostgreSqlStorageTests()
    {
        _postgresContainer = new ContainerBuilder()
            .WithImage("postgres:16-alpine")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "cabal_test")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
        var port = _postgresContainer.GetMappedPublicPort(5432);
        var connectionString = $"Host=localhost;Port={port};Username=postgres;Password=postgres;Database=cabal_test";
        _storage = new PostgreSqlJobStorage(connectionString);
        await _storage.InitializeDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public async Task Sync_WhenIntervalChanged_ShouldUpdateIntervalInDatabase()
    {
        var original = new JobDefinition { Name = "Email Sender", Interval = TimeSpan.FromSeconds(60) };
        await _storage!.SyncJobsFromMemoryAsync([original]);

        var updated = new JobDefinition { Name = "Email Sender", Interval = TimeSpan.FromSeconds(120) };
        await _storage.SyncJobsFromMemoryAsync([updated]);

        var stats = await _storage.GetDashboardStatsAsync();
        var jobInDb = stats.Jobs.Single(j => j.Name == "Email Sender");

        jobInDb.IntervalSeconds.Should().Be(120,
            because: "the scheduler must use the current code interval, not the one from first boot");
    }

    [Fact]
    public async Task Sync_WhenJobRemovedFromCode_ShouldDeleteItFromDatabase()
    {
        var jobA = new JobDefinition { Name = "Job A", Interval = TimeSpan.FromSeconds(60) };
        var jobB = new JobDefinition { Name = "Job B", Interval = TimeSpan.FromSeconds(60) };
        await _storage!.SyncJobsFromMemoryAsync([jobA, jobB]);

        await _storage.SyncJobsFromMemoryAsync([jobA]);

        var stats = await _storage.GetDashboardStatsAsync();

        stats.Jobs.Should().ContainSingle(j => j.Name == "Job A");
        stats.Jobs.Should().NotContain(j => j.Name == "Job B",
            because: "a job removed from code must not keep running from the database");
    }

    [Fact]
    public async Task MarkCompleted_HappyPath_ShouldUpdateJobAndWriteHistoryTogether()
    {
        var job = new JobDefinition { Name = "Ping", Interval = TimeSpan.FromSeconds(30) };
        await _storage!.SyncJobsFromMemoryAsync([job]);

        var jobs = await _storage.GetAndLockNextJobsAsync(DateTime.UtcNow.AddSeconds(31), 1);
        var jobId = jobs.FirstOrDefault();
        jobId.Should().NotBeNull();

        await _storage.MarkJobAsCompletedAsync(jobId!, intervalSeconds: 30, success: true, errorMessage: null);

        var stats = await _storage.GetDashboardStatsAsync();

        stats.TotalExecutions.Should().Be(1, because: "there should be exactly one history entry");
        stats.FailedExecutions.Should().Be(0);
        stats.Jobs.Single(j => j.Name == "Ping").NextExecution
            .Should().NotBeNullOrEmpty(because: "NextExecution must have been recalculated");
    }

    [Fact]
    public async Task MarkCompleted_WhenJobDoesNotExist_ShouldNotCreateOrphanHistory()
    {
        var fakeId = Guid.NewGuid().ToString("N");
        await _storage!.MarkJobAsCompletedAsync(fakeId, intervalSeconds: 60, success: true, errorMessage: null);

        var stats = await _storage.GetDashboardStatsAsync();

        stats.History.Should().BeEmpty(
            because: "there should be no history for a job that never existed in the database");
        stats.TotalExecutions.Should().Be(0);
    }

    [Fact]
    public async Task GetAndLockNextJob_ShouldReturnAndLockJob()
    {
        var job = new JobDefinition { Name = "Lockable Job", Interval = TimeSpan.FromSeconds(10) };
        await _storage!.SyncJobsFromMemoryAsync([job]);

        var jobs = await _storage.GetAndLockNextJobsAsync(DateTime.UtcNow.AddSeconds(11), 1);
        var jobId = jobs.FirstOrDefault();
        jobId.Should().NotBeNull();

        var record = await _storage.GetJobByIdAsync(jobId!);
        record.Should().NotBeNull();
        record!.Name.Should().Be("Lockable Job");
    }

    [Fact]
    public async Task GetAndLockNextJob_ShouldReturnNullWhenNoJobsAreReady()
    {
        var job = new JobDefinition { Name = "Future Job", Interval = TimeSpan.FromHours(1) };
        await _storage!.SyncJobsFromMemoryAsync([job]);

        var jobs = await _storage.GetAndLockNextJobsAsync(DateTime.UtcNow, 1);
        var jobId = jobs.FirstOrDefault();
        jobId.Should().BeNull();
    }

    [Fact]
    public async Task MarkCompleted_WithFailure_ShouldRecordErrorInHistory()
    {
        var job = new JobDefinition { Name = "Failing Job", Interval = TimeSpan.FromSeconds(30) };
        await _storage!.SyncJobsFromMemoryAsync([job]);

        var jobs = await _storage.GetAndLockNextJobsAsync(DateTime.UtcNow.AddSeconds(31), 1);
        var jobId = jobs.FirstOrDefault();
        jobId.Should().NotBeNull();

        await _storage.MarkJobAsCompletedAsync(jobId!, intervalSeconds: 30, success: false, errorMessage: "Something went wrong");

        var stats = await _storage.GetDashboardStatsAsync();
        stats.FailedExecutions.Should().Be(1);
        stats.History.Should().ContainSingle(h => h.JobName == "Failing Job" && h.Status == "Error");
        stats.History.Single(h => h.JobName == "Failing Job").ErrorMessage.Should().Be("Something went wrong");
    }

    [Fact]
    public async Task DashboardStats_ShouldReturnCorrectCounts()
    {
        var job1 = new JobDefinition { Name = "Job One", Interval = TimeSpan.FromSeconds(10) };
        var job2 = new JobDefinition { Name = "Job Two", Interval = TimeSpan.FromSeconds(10) };
        await _storage!.SyncJobsFromMemoryAsync([job1, job2]);

        var stats = await _storage.GetDashboardStatsAsync();
        stats.ActiveJobs.Should().Be(2);
        stats.TotalExecutions.Should().Be(0);
        stats.FailedExecutions.Should().Be(0);
    }
}
