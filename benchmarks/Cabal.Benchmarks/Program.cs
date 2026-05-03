using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Cabal.Scheduler.Builder;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;
using Cabal.Scheduler.Worker;
using Cabal.SQLite;
using Cabal.PostgreSQL;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Builders;

[MemoryDiagnoser]
[RankColumn] 
public class SchedulerBenchmarks
{
    private const int JobCount = 1000;
    
    private IJobStorage _cabalSqlite = null!;
    private IJobStorage _cabalPostgres = null!;
    private IScheduler _quartzScheduler = null!;
    private SqliteConnection _keepAlive = null!;
    private IContainer _postgresContainer = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = "Data Source=cabal_bench;Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync();
        _cabalSqlite = new SqliteJobStorage(connectionString);
        await _cabalSqlite.InitializeDatabaseAsync();

        _postgresContainer = new ContainerBuilder()
            .WithImage("postgres:16-alpine")
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "cabal_test")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
            .Build();
        
        await _postgresContainer.StartAsync();
        var port = _postgresContainer.GetMappedPublicPort(5432);
        var pgConn = $"Host=localhost;Port={port};Username=postgres;Password=postgres;Database=cabal_test";
        _cabalPostgres = new PostgreSqlJobStorage(pgConn);
        await _cabalPostgres.InitializeDatabaseAsync();

        GlobalConfiguration.Configuration.UseMemoryStorage();

        var factory = new StdSchedulerFactory();
        _quartzScheduler = await factory.GetScheduler();
        await _quartzScheduler.Start();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _keepAlive?.Dispose();
        await _postgresContainer.DisposeAsync();
    }

    [Benchmark]
    public async Task Cabal_SQLite_SyncJobs()
    {
        var jobs = new List<JobDefinition>();
        for (int i = 0; i < JobCount; i++)
        {
            jobs.Add(new JobDefinition { Name = $"Job_{i}", Interval = TimeSpan.FromMinutes(5) });
        }
        await _cabalSqlite.SyncJobsFromMemoryAsync(jobs);
    }

    [Benchmark]
    public async Task Cabal_PostgreSQL_SyncJobs()
    {
        var jobs = new List<JobDefinition>();
        for (int i = 0; i < JobCount; i++)
        {
            jobs.Add(new JobDefinition { Name = $"Job_{i}", Interval = TimeSpan.FromMinutes(5) });
        }
        await _cabalPostgres.SyncJobsFromMemoryAsync(jobs);
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
            IJobDetail job = Quartz.JobBuilder.Create<DummyQuartzJob>()
                .WithIdentity($"Job_{i}", "BenchmarkGroup")
                .Build();

            ITrigger trigger = Quartz.TriggerBuilder.Create() 
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

[MemoryDiagnoser]
[RankColumn]
public class ExecutionBenchmarks
{
    private const int JobCount = 1000;
    
    private IJobStorage _cabalStorage = null!;
    private SchedulerBackgroundService _cabalWorker = null!;
    private IScheduler _quartzScheduler = null!;
    private SqliteConnection _keepAlive = null!;
    private BackgroundJobServer _hangfireServer = null!;
    
    public static int ExecutedCabalJobs = 0;
    public static int ExecutedHangfireJobs = 0;
    public static int ExecutedQuartzJobs = 0;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = "Data Source=cabal_exec_bench;Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync();
        
        _cabalStorage = new SqliteJobStorage(connectionString);
        await _cabalStorage.InitializeDatabaseAsync();

        Schedule.ConsumeJobs(); 
        
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        _cabalWorker = new SchedulerBackgroundService(
            _cabalStorage, 
            NullLogger<SchedulerBackgroundService>.Instance, 
            scopeFactory, 
            TimeSpan.FromMilliseconds(50), 
            maxConcurrentJobs: 100, 
            batchSize: 100);

        GlobalConfiguration.Configuration.UseMemoryStorage();
        _hangfireServer = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 100 });

        var factory = new StdSchedulerFactory();
        _quartzScheduler = await factory.GetScheduler();
        await _quartzScheduler.Start();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hangfireServer?.Dispose();
        _keepAlive?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task Cabal_ExecuteJobs()
    {
        ExecutedCabalJobs = 0;
        var jobs = new List<JobDefinition>();
        Schedule.ConsumeJobs(); // clear
        for (int i = 0; i < JobCount; i++)
        {
            Schedule.Every(0).Seconds().WithName($"CabalJob_{i}").Do(() => Interlocked.Increment(ref ExecutedCabalJobs));
        }
        var scheduledJobs = Schedule.ConsumeJobs();
        await _cabalStorage.SyncJobsFromMemoryAsync(scheduledJobs);
        
        using var cts = new CancellationTokenSource();
        var workerTask = _cabalWorker.StartAsync(cts.Token);
        
        while (Volatile.Read(ref ExecutedCabalJobs) < JobCount)
        {
            await Task.Delay(10);
        }
        
        cts.Cancel();
        await _cabalWorker.StopAsync(CancellationToken.None);
    }

    [Benchmark]
    public async Task Hangfire_ExecuteJobs()
    {
        ExecutedHangfireJobs = 0;
        for (int i = 0; i < JobCount; i++)
        {
            BackgroundJob.Enqueue(() => DummyHangfireMethod());
        }

        while (Volatile.Read(ref ExecutedHangfireJobs) < JobCount)
        {
            await Task.Delay(10);
        }
    }
    
    public static void DummyHangfireMethod()
    {
        Interlocked.Increment(ref ExecutedHangfireJobs);
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Running benchmarks...");
        BenchmarkRunner.Run<SchedulerBenchmarks>();
        BenchmarkRunner.Run<ExecutionBenchmarks>();
    }
}