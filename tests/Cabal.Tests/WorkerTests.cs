using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cabal.Scheduler.Core;
using Cabal.Scheduler.Storage;
using Cabal.Scheduler.Worker;
using Cabal.Scheduler.Builder;

namespace Cabal.Tests;

public class WorkerTests
{
    [Fact]
    public async Task TareaQueLanzaExcepcion_DeberiaMarcarseComoErrorSinTumbarElMotor()
    {
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance; 

        Schedule.ConsumeJobs();
        
        Schedule.Every(1).Minutes().WithName("Bomb task").Do(() => throw new Exception("¡Bum!"));
        
        var jobDefinition = Schedule.PendingJobs.First();

        mockStorage.GetAndLockNextJobAsync(Arg.Any<DateTime>()).Returns(jobDefinition.Id);
        mockStorage.GetJobByIdAsync(jobDefinition.Id).Returns(new JobDefinitionRecord(jobDefinition.Id, "Bomb task"));

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory);

        await worker.StartAsync(CancellationToken.None);
        
        await Task.Delay(100); 
        
        await worker.StopAsync(CancellationToken.None);

        mockStorage.Received().MarkJobAsCompletedAsync(
            jobId: jobDefinition.Id, 
            intervalSeconds: 60, 
            success: false, 
            errorMessage: "¡Bum!"
        );
    }
}