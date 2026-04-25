using Cabal.Scheduler.Builder;
using FluentAssertions;

namespace Cabal.Tests;

public class BuilderTests
{
    [Fact]
    public void Schedule_Every_ShouldStoreJobInMemory()
    {
        Schedule.ConsumeJobs(); 

        Schedule.Every(5).Minutes()
                .WithName("Cleaning test")
                .WithRetries(3)
                .WithTimeout(TimeSpan.FromMinutes(10))
                .Do(() => { });

        var pendingJobs = Schedule.ConsumeJobs().ToList();

        pendingJobs.Should().HaveCount(1);
        var job = pendingJobs.First();
        
        job.Name.Should().Be("Cleaning test");
        job.Interval.Should().Be(TimeSpan.FromMinutes(5));
        job.MaxRetries.Should().Be(3);
        job.LockTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }
}