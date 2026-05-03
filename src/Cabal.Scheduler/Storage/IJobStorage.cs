namespace Cabal.Scheduler.Storage;

using Cabal.Scheduler.Core;
  
public interface IJobStorage
{
    Task InitializeDatabaseAsync();
    Task SyncJobsFromMemoryAsync(IEnumerable<JobDefinition> jobs);
    Task<IReadOnlyList<string>> GetAndLockNextJobsAsync(DateTime now, int limit);
    Task<JobDefinitionRecord?> GetJobByIdAsync(string id);
    Task MarkJobAsCompletedAsync(string jobId, int intervalSeconds, bool success, string? errorMessage);
    Task<DashboardStats> GetDashboardStatsAsync();
}
 