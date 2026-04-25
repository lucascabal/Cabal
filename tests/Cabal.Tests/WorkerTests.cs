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

        mockStorage.GetAndLockNextJobAsync(Arg.Any<DateTime>()).Returns(jobDefinition.Id);
        mockStorage.GetJobByIdAsync(jobDefinition.Id).Returns(new JobDefinitionRecord(jobDefinition.Id, "Bomb task", 60));

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory, TimeSpan.FromMilliseconds(50));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        await mockStorage.Received().MarkJobAsCompletedAsync(
            jobId: jobDefinition.Id,
            intervalSeconds: 60,
            success: false,
            errorMessage: "Boom!"
        );
    }

    /// <summary>
    /// If storage returns a jobId whose Name has no registered delegate in memory,
    /// the worker does a bare `return` without releasing the lock.
    /// The job stays blocked for 5 minutes.
    /// MarkJobAsCompletedAsync MUST be called to release the lock.
    /// </summary>
    [Fact]
    public async Task ProcessNextJob_WhenDelegateNotRegistered_ShouldReleaseTheLock()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance;

        Schedule.ConsumeJobs();

        var orphanJobId = Guid.NewGuid().ToString("N");

        mockStorage.GetAndLockNextJobAsync(Arg.Any<DateTime>())
            .Returns(orphanJobId, (string?)null);
        mockStorage.GetJobByIdAsync(orphanJobId)
            .Returns(new JobDefinitionRecord(orphanJobId, "Unregistered Job", 60));

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

    /// <summary>
    /// An orphan job (no delegate) must not block legitimate jobs from running
    /// on the next tick.
    /// </summary>
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

        mockStorage.GetAndLockNextJobAsync(Arg.Any<DateTime>())
            .Returns(orphanJobId, normalJob.Id, (string?)null);
        mockStorage.GetJobByIdAsync(orphanJobId)
            .Returns(new JobDefinitionRecord(orphanJobId, "Unregistered Job", 60));
        mockStorage.GetJobByIdAsync(normalJob.Id)
            .Returns(new JobDefinitionRecord(normalJob.Id, "Normal Job", 60));

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
}