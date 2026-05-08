using System;
using System.Threading;
using System.Threading.Tasks;
using Cabal.Scheduler.Builder;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;
using Cabal.Scheduler.Worker;
using Cabal.SQLite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;

public class Program
{
    public static async Task Main()
    {
        var connectionString = "Data Source=cabal_exec_bench2;Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        
        var storage = new SqliteJobStorage(connectionString);
        await storage.InitializeDatabaseAsync();

        Schedule.ConsumeJobs(); 
        
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var worker = new SchedulerBackgroundService(
            storage, 
            NullLogger<SchedulerBackgroundService>.Instance, 
            scopeFactory, 
            TimeSpan.FromMilliseconds(50), 
            maxConcurrentJobs: 100, 
            batchSize: 100);

        int executed = 0;
        for (int i = 0; i < 100; i++)
        {
            Schedule.Every(1).Seconds().WithName($"CabalJob_{i}").Do(() => Interlocked.Increment(ref executed));
        }
        var scheduledJobs = Schedule.ConsumeJobs();
        await storage.SyncJobsFromMemoryAsync(scheduledJobs);
        
        await using var cmd = keepAlive.CreateCommand();
        cmd.CommandText = "UPDATE ScheduledJobs SET NextExecution = datetime('now', '-1 day');";
        await cmd.ExecuteNonQueryAsync();
        
        using var cts = new CancellationTokenSource();
        var workerTask = worker.StartAsync(cts.Token);
        
        int attempts = 0;
        while (Volatile.Read(ref executed) < 100 && attempts < 50)
        {
            await Task.Delay(100);
            attempts++;
            Console.WriteLine($"Executed: {executed}");
        }
        
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        Console.WriteLine($"Final Executed: {executed}");
    }
}
