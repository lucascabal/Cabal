using Microsoft.Extensions.DependencyInjection;
using Cabal.Scheduler.Storage;

namespace Cabal.SQLite;

public static class CabalSqliteExtensions
{
    public static IServiceCollection AddCabalSqlite(this IServiceCollection services, string connectionString = "Data Source=cabal.db;")
    {
        services.AddSingleton<IJobStorage>(new SqliteJobStorage(connectionString));
        
        services.AddHostedService<Cabal.Scheduler.Worker.SchedulerBackgroundService>();
        
        return services;
    }
}