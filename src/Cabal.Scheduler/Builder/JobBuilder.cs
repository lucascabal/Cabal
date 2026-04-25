using System;
using System.Threading;
using System.Threading.Tasks;
using Cabal.Scheduler.Core;

namespace Cabal.Scheduler.Builder;

public class JobBuilder
{
    private readonly TimeSpan _interval;
    private string _jobName = "Anonymous Job";
    private int _maxRetries = 0;
    private TimeSpan _lockTimeout = TimeSpan.FromMinutes(5);

    internal JobBuilder(TimeSpan interval) => _interval = interval;

    public JobBuilder WithName(string name)
    {
        _jobName = string.IsNullOrWhiteSpace(name) ? "Anonymous Job" : name;
        return this;
    }

    public JobBuilder WithRetries(int maxRetries)
    {
        _maxRetries = maxRetries < 0 ? 0 : maxRetries;
        return this;
    }

    public JobBuilder WithTimeout(TimeSpan timeout)
    {
        _lockTimeout = timeout;
        return this;
    }

    public void Do(Func<IServiceProvider, CancellationToken, Task> action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        var definition = new JobDefinition
        {
            Name = _jobName,
            Interval = _interval,
            MaxRetries = _maxRetries,
            LockTimeout = _lockTimeout,
            ActionToExecute = action
        };

        Schedule.PendingJobs.Add(definition);
    }

    public void Do(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        Do((_, _) => 
        {
            action();
            return Task.CompletedTask;
        });
    }
}