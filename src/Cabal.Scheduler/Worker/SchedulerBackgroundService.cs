using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cabal.Scheduler.Builder;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;

namespace Cabal.Scheduler.Worker;

public class SchedulerBackgroundService : BackgroundService
{
    private readonly IJobStorage _storage;
    private readonly ILogger<SchedulerBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _pollingInterval;

    private readonly Dictionary<string, JobDefinition> _jobDelegates = [];

    public SchedulerBackgroundService(
        IJobStorage storage,
        ILogger<SchedulerBackgroundService> logger,
        IServiceScopeFactory scopeFactory,
        TimeSpan? pollingInterval = null)
    {
        _storage = storage;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cabal Scheduler: Starting engine...");

        await _storage.InitializeDatabaseAsync();

        var registeredJobs = Schedule.ConsumeJobs();
        foreach (var job in registeredJobs)
        {
            _jobDelegates[job.Name] = job;
        }

        await _storage.SyncJobsFromMemoryAsync(registeredJobs);

        using var timer = new PeriodicTimer(_pollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessNextJobAsync(stoppingToken);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessNextJobAsync(CancellationToken stoppingToken)
    {
        var jobId = await _storage.GetAndLockNextJobAsync(DateTime.UtcNow);
        if (jobId == null) return;

        var jobRecord = await _storage.GetJobByIdAsync(jobId);
        if (jobRecord == null || !_jobDelegates.TryGetValue(jobRecord.Name, out var definition))
        {
            _logger.LogWarning("Cabal: Job {JobId} found in storage but has no registered action. Releasing lock.", jobId);
            var interval = jobRecord?.IntervalSeconds ?? 0;
            await _storage.MarkJobAsCompletedAsync(jobId, interval, success: false, errorMessage: "No delegate registered for this job.");
            return;
        }

        bool success = false;
        string? errorMessage = null;
        int currentAttempt = 0;
        int maxAttempts = definition.MaxRetries + 1;

        var stopwatch = Stopwatch.StartNew();

        while (currentAttempt < maxAttempts && !success && !stoppingToken.IsCancellationRequested)
        {
            currentAttempt++;
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    await definition.ActionToExecute(scope.ServiceProvider, stoppingToken);
                }
                success = true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                _logger.LogWarning("Cabal: [{JobName}] failed (attempt {Attempt}/{Max}). {Error}",
                    definition.Name, currentAttempt, maxAttempts, ex.Message);

                if (currentAttempt < maxAttempts && !stoppingToken.IsCancellationRequested)
                {
                    var delaySeconds = Math.Pow(2, currentAttempt);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                }
            }
        }

        stopwatch.Stop();

        if (!success)
        {
            _logger.LogError("Cabal: [{JobName}] failed after {Max} attempts.", definition.Name, maxAttempts);
        }
        else
        {
            _logger.LogInformation("Cabal: [{JobName}] completed in {Ms}ms.", definition.Name, stopwatch.ElapsedMilliseconds);
        }

        await _storage.MarkJobAsCompletedAsync(jobId, (int)definition.Interval.TotalSeconds, success, errorMessage);
    }
}