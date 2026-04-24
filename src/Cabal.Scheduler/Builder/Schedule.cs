using System.Collections.Concurrent;
using System.Collections.Generic;
using Cabal.Scheduler.Core;

namespace Cabal.Scheduler.Builder;

public static class Schedule
{
    internal static ConcurrentBag<JobDefinition> PendingJobs { get; } = [];

    public static IntervalBuilder Every(int value)
    {
        return new IntervalBuilder(value);
    }

    internal static IEnumerable<JobDefinition> ConsumeJobs()
    {
        var jobs = PendingJobs.ToArray();
        PendingJobs.Clear();
        return jobs;
    }
}