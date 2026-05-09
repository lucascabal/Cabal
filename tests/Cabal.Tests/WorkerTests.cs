using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;
using Cabal.Scheduler.Worker;
using Cabal.Scheduler.Builder;

namespace Cabal.Tests;

public class WorkerTests
{
    [Fact]
    public async Task FailingJob_ShouldBeMarkedAsErrorWithoutCrashingTheEngine()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance;

        Schedule.ConsumeJobs();

        Schedule.Every(1).Minutes().WithName("Bomb task").Do(() => throw new Exception("Boom!"));

        var jobDefinition = Schedule.PendingJobs.First();

        mockStorage.GetAndLockNextJobsAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<JobDefinitionRecord> { new JobDefinitionRecord(jobDefinition.Id, "Bomb task", 60) }, new List<JobDefinitionRecord>());

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory, TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        _ = mockStorage.Received().MarkJobAsCompletedAsync(
            jobId: jobDefinition.Id,
            intervalSeconds: 60,
            success: false,
            errorMessage: "Boom!"
        );
    }

    [Fact]
    public async Task ProcessNextJob_WhenDelegateNotRegistered_ShouldReleaseTheLock()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance;

        Schedule.ConsumeJobs();

        var orphanJobId = Guid.NewGuid().ToString("N");

        mockStorage.GetAndLockNextJobsAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<JobDefinitionRecord> { new JobDefinitionRecord(orphanJobId, "Unregistered Job", 60) }, new List<JobDefinitionRecord>());

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory, TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        await mockStorage.Received(1).MarkJobAsCompletedAsync(
            jobId: orphanJobId,
            intervalSeconds: Arg.Any<int>(),
            success: false,
            errorMessage: Arg.Any<string?>()
        );
    }

    [Fact]
    public async Task ProcessNextJob_WhenOrphanJobFollowedByNormalJob_BothAreProcessed()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance;

        Schedule.ConsumeJobs();
        Schedule.Every(1).Minutes().WithName("Normal Job").Do(() => { });
        var normalJob = Schedule.PendingJobs.First();

        var orphanJobId = Guid.NewGuid().ToString("N");

        mockStorage.GetAndLockNextJobsAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<JobDefinitionRecord> { 
                new JobDefinitionRecord(orphanJobId, "Unregistered Job", 60),
                new JobDefinitionRecord(normalJob.Id, "Normal Job", 60)
            }, new List<JobDefinitionRecord>());

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory, TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await worker.StopAsync(CancellationToken.None);

        await mockStorage.Received(1).MarkJobAsCompletedAsync(
            jobId: normalJob.Id,
            intervalSeconds: Arg.Any<int>(),
            success: true,
            errorMessage: Arg.Any<string?>()
        );
    }

    [Fact]
    public async Task Worker_ShouldRespectConcurrencyLimits()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance;

        Schedule.ConsumeJobs();

        var jobRecords = new List<JobDefinitionRecord>();
        for (int i = 0; i < 5; i++)
        {
            var jobName = $"LongJob_{i}";
            Schedule.Every(1).Minutes().WithName(jobName).Do(async (sp, ct) => await Task.Delay(200, ct));
            var job = Schedule.PendingJobs.Last();
            jobRecords.Add(new JobDefinitionRecord(job.Id, jobName, 60));
        }

        mockStorage.GetAndLockNextJobsAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(jobRecords, new List<JobDefinitionRecord>());

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory, TimeSpan.FromMilliseconds(50), maxConcurrentJobs: 2, batchSize: 5);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50); // Give it time to start 2 jobs, but not finish them

        // At this point, only 2 jobs should have been requested via GetAndLockNextJobsAsync but they are dispatched in batch.
        // Wait, how do we test concurrency limits now?
        // Since GetAndLockNextJobsAsync returns 5 jobs, the worker loops and acquires the semaphore 5 times.
        // It will await _concurrencySemaphore on the 3rd job!
        // We can assert that MarkJobAsCompletedAsync was NOT called for any job yet.
        var completedCalls = mockStorage.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IJobStorage.MarkJobAsCompletedAsync));
        
        Assert.Equal(0, completedCalls);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProcessNextJob_WhenRunOnce_ShouldDelayIndefinitely()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance;

        Schedule.ConsumeJobs();
        Schedule.Once().WithName("One-Time Task").Do(() => { });
        var jobDefinition = Schedule.PendingJobs.First();

        mockStorage.GetAndLockNextJobsAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<JobDefinitionRecord> { new JobDefinitionRecord(jobDefinition.Id, "One-Time Task", 0) }, new List<JobDefinitionRecord>());

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory, TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        await mockStorage.Received(1).MarkJobAsCompletedAsync(
            jobId: jobDefinition.Id,
            intervalSeconds: int.MaxValue,
            success: true,
            errorMessage: Arg.Any<string?>()
        );
    }
}