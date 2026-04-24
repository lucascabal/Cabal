using Cabal.Scheduler.Builder;
using FluentAssertions;

namespace Cabal.Tests;

public class BuilderTests
{
    [Fact]
    public void Schedule_Every_DeberiaGuardarElTrabajoEnMemoria()
    {
        Schedule.ConsumeJobs(); 

        Schedule.Every(5).Minutes()
                .WithName("Test de Limpieza")
                .WithRetries(3)
                .Do(() => { });

        var pendingJobs = Schedule.ConsumeJobs().ToList();

        pendingJobs.Should().HaveCount(1);
        var job = pendingJobs.First();
        
        job.Name.Should().Be("Test de Limpieza");
        job.Interval.Should().Be(TimeSpan.FromMinutes(5));
        job.MaxRetries.Should().Be(3);
    }
}