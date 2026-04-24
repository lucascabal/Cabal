using System;
using System.Collections.Generic;
using Cabal.Scheduler.Core;

namespace Cabal.Scheduler.Storage;

public interface IJobStorage
{
    void InitializeDatabase();
    void SyncJobsFromMemory(IEnumerable<JobDefinition> jobs);
    string? GetAndLockNextJob(DateTime now);
    JobDefinitionRecord? GetJobById(string id);
    void MarkJobAsCompleted(string jobId, int intervalSeconds, bool success, string? errorMessage);
    DashboardStats GetDashboardStats();
}