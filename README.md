# Cabal Scheduler

A lightweight background job engine for .NET 8. No Redis, no ORMs, no bloat.

Sometimes you just need to run a task every few minutes and know if it crashes. Hangfire and Quartz are great tools, but they come with real setup costs — infrastructure dependencies, massive schemas, learning curves. Cabal is built for the cases where all of that is simply overkill.

---

## ⚡ Features

- **Zero external dependencies** in the core package. The only reference is `Microsoft.AspNetCore.App`.
- **Raw ADO.NET** against SQLite or PostgreSQL. No ORM, no reflection magic.
- **Concurrency-safe.** SQLite uses WAL mode and atomic `UPDATE … RETURNING`. PostgreSQL uses `FOR UPDATE SKIP LOCKED` for native row-level locking.
- **Batch Processing.** Heavily optimized for high-throughput environments. It fetches and locks multiple jobs in a single database roundtrip to drastically reduce latency.
- **Concurrency Limits.** Protects your `ThreadPool` and database connection pools. Configure a hard limit on simultaneous background jobs (defaults to 100) backed by a `SemaphoreSlim`.
- **Exponential Backoff.** Configure max retries per job. Failures are caught, logged, and permanently recorded in the database.
- **Built-in Dashboard.** Mount it at any path you want. No external assets needed, the HTML is embedded directly in the binary.

---

## 📦 Installation

*(Coming soon to NuGet 😉)*
```bash
dotnet add package Cabal.Scheduler
dotnet add package Cabal.SQLite
# or if you prefer PostgreSQL
dotnet add package Cabal.PostgreSQL
```

---

## 🚀 Quick Start

### Using SQLite

```csharp
// Program.cs
using Cabal.Scheduler;
using Cabal.Scheduler.Builder;
using Cabal.SQLite;

var builder = WebApplication.CreateBuilder(args);

// 1. Register Cabal with SQLite storage
builder.Services.AddCabalSqlite("Data Source=cabal.db;");

// 2. Define your jobs
Schedule.Every(5).Seconds()
        .WithName("System Ping")
        .Do(() => Console.WriteLine("Ping!"));

Schedule.Every(1).Days()
        .WithName("DB Backup")
        .WithRetries(3)
        .Do(async (services, ct) => await RunBackupAsync(ct));

var app = builder.Build();

// 3. Mount the dashboard
app.UseCabalDashboard("/cabal");

app.Run();
```

### Using PostgreSQL

```csharp
// Program.cs
using Cabal.Scheduler;
using Cabal.Scheduler.Builder;
using Cabal.PostgreSQL;

var builder = WebApplication.CreateBuilder(args);

// 1. Register Cabal with PostgreSQL storage
builder.Services.AddCabalPostgreSql("Host=localhost;Port=5432;Database=cabal;Username=postgres;Password=postgres;");

// 2. Define your jobs (same API as SQLite)
Schedule.Every(5).Seconds()
        .WithName("System Ping")
        .Do(() => Console.WriteLine("Ping!"));

var app = builder.Build();

// 3. Mount the dashboard
app.UseCabalDashboard("/cabal");

app.Run();
```

> **Tip:** Use the `compose.yaml` file in this repo to easily spin up a PostgreSQL instance for local development:
> ```bash
> docker compose up -d
> ```

---

## 📊 Dashboard

Navigate to `/cabal` (or whatever route you configured) to monitor active jobs, check next execution times, view the history, and see an RPM graph for the last hour.

<p align="center">
  <img src="assets/dashboard.png" alt="Cabal dashboard" width="800">
</p>

---

## 💉 Scoped Services

Every job execution automatically runs within its own Dependency Injection scope. This means you can safely resolve scoped services like an Entity Framework `DbContext`:

```csharp
Schedule.Every(10).Minutes()
        .WithName("Send Pending Emails")
        .Do(async (services, ct) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            // Your logic here...
        });
```

---

## 📄 License

MIT
