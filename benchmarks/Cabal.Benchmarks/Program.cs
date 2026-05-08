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
    [Params(100, 1000)]
    public int JobCount { get; set; }
    
    private IJobStorage _cabalSqlite = null!;
    private IJobStorage _cabalPostgres = null!;
    private IScheduler _quartzScheduler = null!;
    private SqliteConnection _keepAlive = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = "Data Source=cabal_bench;Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync();
        _cabalSqlite = new SqliteJobStorage(connectionString);
        await _cabalSqlite.InitializeDatabaseAsync();

        var pgConn = "Host=localhost;Port=5432;Username=postgres;Password=admin;Database=postgres";
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
    [Params(100, 1000)]
    public int JobCount { get; set; }
    
    private IJobStorage _cabalStorage = null!;
    private SchedulerBackgroundService _cabalWorker = null!;
    private IScheduler _quartzScheduler = null!;
    private SqliteConnection _keepAlive = null!;
    private BackgroundJobServer _hangfireServer = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    
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
        _scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

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
        Schedule.ConsumeJobs(); // clear
        for (int i = 0; i < JobCount; i++)
        {
            Schedule.Every(1).Seconds().WithName($"CabalJob_{i}").Do(() => Interlocked.Increment(ref ExecutedCabalJobs));
        }

        var worker = new SchedulerBackgroundService(
            _cabalStorage, 
            NullLogger<SchedulerBackgroundService>.Instance, 
            _scopeFactory, 
            TimeSpan.FromMilliseconds(50), 
            maxConcurrentJobs: 100, 
            batchSize: 100);

        // worker.StartAsync will ConsumeJobs, Sync them, and set NextExecution to +1 second.
        // We start the worker, but we need to update NextExecution so they run immediately.
        using var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);
        
        // Wait briefly for the worker to initialize the DB and sync jobs
        await Task.Delay(100);
        
        await using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET NextExecution = datetime('now', '-1 day');";
        await cmd.ExecuteNonQueryAsync();
        
        while (Volatile.Read(ref ExecutedCabalJobs) < JobCount)
        {
            await Task.Delay(10);
        }
        
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
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
    
    [Benchmark]
    public async Task Quartz_ExecuteJobs()
    {
        ExecutedQuartzJobs = 0;
        await _quartzScheduler.Clear();

        for (int i = 0; i < JobCount; i++)
        {
            IJobDetail job = Quartz.JobBuilder.Create<DummyQuartzExecutionJob>()
                .WithIdentity($"ExecJob_{i}", "BenchmarkExecGroup")
                .Build();

            ITrigger trigger = Quartz.TriggerBuilder.Create()
                .WithIdentity($"ExecTrigger_{i}", "BenchmarkExecGroup")
                .StartNow()
                .Build();

            await _quartzScheduler.ScheduleJob(job, trigger);
        }

        while (Volatile.Read(ref ExecutedQuartzJobs) < JobCount)
        {
            await Task.Delay(10);
        }
    }

    public static void DummyHangfireMethod()
    {
        Interlocked.Increment(ref ExecutedHangfireJobs);
    }

    public class DummyQuartzExecutionJob : IJob
    {
        public Task Execute(IJobExecutionContext context)
        {
            Interlocked.Increment(ref ExecutedQuartzJobs);
            return Task.CompletedTask;
        }
    }
}

[MemoryDiagnoser]
[RankColumn]
public class FireAndForgetBenchmarks
{
    [Params(10000)]
    public int JobCount { get; set; }
    
    private BackgroundJobServer _hangfireServer = null!;
    private IJobStorage _cabalStorage = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private SqliteConnection _keepAlive = null!;

    public static int ExecutedHangfireJobs = 0;
    public static int ExecutedCabalJobs = 0;

    [GlobalSetup]
    public async Task Setup()
    {
        var connectionString = "Data Source=cabal_ff_bench;Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(connectionString);
        await _keepAlive.OpenAsync();
        
        _cabalStorage = new SqliteJobStorage(connectionString);
        await _cabalStorage.InitializeDatabaseAsync();

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        _scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        GlobalConfiguration.Configuration.UseMemoryStorage();
        _hangfireServer = new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 100 });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _hangfireServer?.Dispose();
        _keepAlive?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task Hangfire_FireAndForget()
    {
        ExecutedHangfireJobs = 0;
        for (int i = 0; i < JobCount; i++)
        {
            BackgroundJob.Enqueue(() => DummyHangfireFFMethod());
        }

        while (Volatile.Read(ref ExecutedHangfireJobs) < JobCount)
        {
            await Task.Delay(10);
        }
    }

    [Benchmark]
    public async Task Cabal_FireAndForget_Simulated()
    {
        ExecutedCabalJobs = 0;
        Schedule.ConsumeJobs(); // clear
        for (int i = 0; i < JobCount; i++)
        {
            Schedule.Every(1).Seconds().WithName($"CabalJob_{i}").Do(() => Interlocked.Increment(ref ExecutedCabalJobs));
        }

        var worker = new SchedulerBackgroundService(
            _cabalStorage, 
            NullLogger<SchedulerBackgroundService>.Instance, 
            _scopeFactory, 
            TimeSpan.FromMilliseconds(10), 
            maxConcurrentJobs: 100, 
            batchSize: 100);

        using var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);
        
        await Task.Delay(200); // Give worker time to initialize
        
        await using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET NextExecution = datetime('now', '-1 day');";
        await cmd.ExecuteNonQueryAsync();

        while (Volatile.Read(ref ExecutedCabalJobs) < JobCount)
        {
            await Task.Delay(10);
        }
        
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    public static void DummyHangfireFFMethod()
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
        BenchmarkRunner.Run<FireAndForgetBenchmarks>();
    }
}