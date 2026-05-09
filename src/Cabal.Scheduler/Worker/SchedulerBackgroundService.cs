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
    private readonly int _batchSize;
    private readonly SemaphoreSlim _concurrencySemaphore;

    private readonly Dictionary<string, JobDefinition> _jobDelegates = [];

    public SchedulerBackgroundService(
        IJobStorage storage,
        ILogger<SchedulerBackgroundService> logger,
        IServiceScopeFactory scopeFactory,
        TimeSpan? pollingInterval = null,
        int maxConcurrentJobs = 100,
        int batchSize = 50)
    {
        _storage = storage;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(5);
        _batchSize = batchSize;
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);
        _maxConcurrentJobs = maxConcurrentJobs;
    }

    private readonly int _maxConcurrentJobs;

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

        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    await _storage.CleanupOldHistoryAsync(DateTime.UtcNow.AddDays(-7));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cabal: Error cleaning up history.");
                }
            }
        }, stoppingToken);

        using var timer = new PeriodicTimer(_pollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var hasMore = await TryDispatchNextJobsBatchAsync(stoppingToken);
            
            if (hasMore)
            {
                continue;
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Cabal Scheduler: Awaiting active jobs to complete before shutdown...");
        for (int i = 0; i < _maxConcurrentJobs; i++)
        {
            await _concurrencySemaphore.WaitAsync();
        }
        _logger.LogInformation("Cabal Scheduler: All active jobs completed. Engine stopped.");
    }

    private async Task<bool> TryDispatchNextJobsBatchAsync(CancellationToken stoppingToken)
    {
        var jobRecords = await _storage.GetAndLockNextJobsAsync(DateTime.UtcNow, _batchSize);
        if (jobRecords == null || jobRecords.Count == 0) return false;

        foreach (var jobRecord in jobRecords)
        {
            await _concurrencySemaphore.WaitAsync(stoppingToken);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!_jobDelegates.TryGetValue(jobRecord.Name, out var definition))
                    {
                        _logger.LogWarning("Cabal: Job {JobId} found in storage but has no registered action. Releasing lock.", jobRecord.Id);
                        var interval = jobRecord.IntervalSeconds;
                        await _storage.MarkJobAsCompletedAsync(jobRecord.Id, interval, success: false, errorMessage: "No delegate registered for this job.");
                        return;
                    }

                    bool success = false;
                    string? errorMessage = null;
                    int currentAttempt = 0;
                    int maxAttempts = definition.MaxRetries + 1;

                    var stopwatch = Stopwatch.StartNew();

                    try
                    {
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
                                    try
                                    {
                                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        errorMessage = ex.Message;
                    }
                    finally
                    {
                        stopwatch.Stop();

                        if (!success)
                        {
                            _logger.LogError("Cabal: [{JobName}] failed after {Max} attempts.", definition.Name, maxAttempts);
                        }
                        else
                        {
                            _logger.LogDebug("Cabal: [{JobName}] completed in {Ms}ms.", definition.Name, stopwatch.ElapsedMilliseconds);
                        }

                        var intervalSeconds = (int)definition.Interval.TotalSeconds;
                        if (definition.RunOnce && success)
                        {
                            // Delay next execution indefinitely
                            intervalSeconds = int.MaxValue;
                        }

                        await _storage.MarkJobAsCompletedAsync(jobRecord.Id, intervalSeconds, success, errorMessage);
                    }
                }
                finally
                {
                    _concurrencySemaphore.Release();
                }
            });
        }

        return jobRecords.Count == _batchSize;
    }
}