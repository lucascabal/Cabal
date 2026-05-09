namespace Cabal.Scheduler.Storage;

using Cabal.Scheduler.Core;
  
public interface IJobStorage
{
    Task InitializeDatabaseAsync();
    Task SyncJobsFromMemoryAsync(IEnumerable<JobDefinition> jobs);
    Task<IReadOnlyList<JobDefinitionRecord>> GetAndLockNextJobsAsync(DateTime now, int limit);
    Task MarkJobAsCompletedAsync(string jobId, int intervalSeconds, bool success, string? errorMessage);
    Task CleanupOldHistoryAsync(DateTime cutoff);
    Task<DashboardStats> GetDashboardStatsAsync();
}