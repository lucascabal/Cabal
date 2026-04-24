using System.Collections.Generic;

namespace Cabal.Scheduler.Core;

public record GraphPoint(long Timestamp, int Executions);
public record JobInfo(string Name, int IntervalSeconds, string NextExecution, string? LockedUntil);
public record JobHistoryLog(string JobName, string ExecutedAt, string Status, string? ErrorMessage);

public record DashboardStats(
    int ActiveJobs,
    string Uptime,
    int TotalExecutions,
    int FailedExecutions,
    List<JobInfo> Jobs,
    List<JobHistoryLog> History,
    List<GraphPoint> PerformanceGraph
);