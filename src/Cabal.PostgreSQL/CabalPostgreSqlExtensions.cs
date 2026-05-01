using Microsoft.Extensions.DependencyInjection;
using Cabal.Scheduler.Storage;

namespace Cabal.PostgreSQL;

public static class CabalPostgreSqlExtensions
{
    public static IServiceCollection AddCabalPostgreSql(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IJobStorage>(new PostgreSqlJobStorage(connectionString));
        services.AddHostedService<Cabal.Scheduler.Worker.SchedulerBackgroundService>();
        return services;
    }
}
