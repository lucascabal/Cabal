using Cabal.Scheduler;
using Cabal.Scheduler.Builder;
using Cabal.SQLite;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Cabal with SQLite storage
builder.Services.AddCabalSqlite("Data Source=cabal.db;");

// 2. Define your jobs
Schedule.Every(5).Seconds()
        .WithName("System Ping")
        .Do(() => Console.WriteLine("Ping OK!"));

Schedule.Every(15).Minutes()
        .WithName("Cache Cleanup")
        .WithRetries(3) // Auto-retry on failure
        .Do(async () => 
        {
            await Task.Delay(100); // Simulate work
            // Throwing an exception here will be safely caught and logged to the DB
        });

var app = builder.Build();

// 3. Map the dashboard endpoint
app.UseCabalDashboard("/cabal");

app.Run();