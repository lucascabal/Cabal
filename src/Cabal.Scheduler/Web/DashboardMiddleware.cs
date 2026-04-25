using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Cabal.Scheduler.Storage;

namespace Cabal.Scheduler.Web;

internal class DashboardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _path;

    public DashboardMiddleware(RequestDelegate next, string path)
    {
        _next = next;
        _path = path;
    }

    public async Task InvokeAsync(HttpContext context, IJobStorage storage)
    {
        if (context.Request.Path.StartsWithSegments(_path, out var remaining))
        {
            if (!remaining.HasValue || remaining.Value == "/")
            {
                await ServeHtmlAsync(context);
                return;
            }
            
            if (remaining.Value == "/api/stats")
            {
                await ServeApiStatsAsync(context, storage);
                return;
            }
        }
        
        await _next(context);
    }

    private async Task ServeHtmlAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Cabal.Scheduler.Web.UI.dashboard.html");
        
        if (stream == null)
        {
            await context.Response.WriteAsync("Error: Html resource not found.");
            return;
        }

        using var reader = new StreamReader(stream);
        await context.Response.WriteAsync(await reader.ReadToEndAsync());
    }

    private async Task ServeApiStatsAsync(HttpContext context, IJobStorage storage)
    {
        context.Response.ContentType = "application/json";
        
        var stats = await storage.GetDashboardStatsAsync(); 

        await context.Response.WriteAsync(JsonSerializer.Serialize(stats));
    }
}