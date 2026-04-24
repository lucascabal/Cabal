using Microsoft.AspNetCore.Builder;

namespace Cabal.Scheduler;

public static class CabalWebExtensions
{
    public static IApplicationBuilder UseCabalDashboard(this IApplicationBuilder app, string path = "/cabal")
    {
        return app.UseMiddleware<Web.DashboardMiddleware>(path);
    }
}