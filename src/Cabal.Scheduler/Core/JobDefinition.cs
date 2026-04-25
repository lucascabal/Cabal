namespace Cabal.Scheduler.Core;

public class JobDefinition
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Unnamed Job";
    public TimeSpan Interval { get; set; }
    public int MaxRetries { get; set; } = 0;
    
    public Func<IServiceProvider, CancellationToken, Task> ActionToExecute { get; set; } = (_, _) => Task.CompletedTask;
}