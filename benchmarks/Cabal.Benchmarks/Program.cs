using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;
using Cabal.SQLite;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Data.Sqlite;
using Quartz;
using Quartz.Impl;

[MemoryDiagnoser]
[RankColumn] 
public class SchedulerBenchmarks
{
    private const int JobCount = 1000;
    
    private IJobStorage _cabalStorage = null!;
    private IScheduler _quartzScheduler = null!;
    private SqliteConnection _keepAlive = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = "Data Source=cabal_bench;Mode=Memory;Cache=Shared";
        
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync();
        
        _cabalStorage = new SqliteJobStorage(connectionString);
        await _cabalStorage.InitializeDatabaseAsync();

        GlobalConfiguration.Configuration.UseMemoryStorage();

        var factory = new StdSchedulerFactory();
        _quartzScheduler = await factory.GetScheduler();
        await _quartzScheduler.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _keepAlive?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task Cabal_SyncJobs()
    {
        var jobs = new List<JobDefinition>();
        for (int i = 0; i < JobCount; i++)
        {
            var job = new JobDefinition
            {
                Name = $"Job_{i}",
                Interval = TimeSpan.FromMinutes(5),
                ActionToExecute = (_, _) => Task.CompletedTask
            };
            jobs.Add(job);
        }

        await _cabalStorage.SyncJobsFromMemoryAsync(jobs);
    }

    [Benchmark]
    public void Hangfire_EnqueueJobs()
    {
        for (int i = 0; i < JobCount; i++)
        {
            RecurringJob.AddOrUpdate(
                $"Job_{i}",
                () => Console.WriteLine("Dummy"), 
                "*/5 * * * *" 
            );
        }
    }

    [Benchmark]
    public async Task Quartz_ScheduleJobs()
    {
        await _quartzScheduler.Clear(); 

        for (int i = 0; i < JobCount; i++)
        {
            IJobDetail job = JobBuilder.Create<DummyQuartzJob>()
                .WithIdentity($"Job_{i}", "BenchmarkGroup")
                .Build();

            ITrigger trigger = Quartz.TriggerBuilder.Create() // Especificamos Quartz.TriggerBuilder
                .WithIdentity($"Trigger_{i}", "BenchmarkGroup")
                .StartNow()
                .WithSimpleSchedule(x => x.WithIntervalInMinutes(5).RepeatForever())
                .Build();

            await _quartzScheduler.ScheduleJob(job, trigger);
        }
    }

    public class DummyQuartzJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<SchedulerBenchmarks>();
    }
}