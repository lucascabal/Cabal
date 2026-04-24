using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
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
        // 1. ARRANGE
        var mockStorage = Substitute.For<IJobStorage>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<SchedulerBackgroundService>.Instance; 

        Schedule.ConsumeJobs(); // Limpiamos la memoria
        
        Schedule.Every(1).Minutes().WithName("Tarea Bomba").Do(() => throw new Exception("¡Bum!"));
        
        var jobDefinition = Schedule.PendingJobs.First();

        mockStorage.GetAndLockNextJob(Arg.Any<DateTime>()).Returns(jobDefinition.Id);
        mockStorage.GetJobById(jobDefinition.Id).Returns(new JobDefinitionRecord(jobDefinition.Id, "Tarea Bomba"));

        var worker = new SchedulerBackgroundService(mockStorage, logger, scopeFactory);

        // 2. ACT
        // Arrancamos el worker
        await worker.StartAsync(CancellationToken.None);
        
        // ¡LA CLAVE! Le damos 100 milisegundos al hilo de fondo para que ejecute la tarea y actualice la BD
        await Task.Delay(100); 
        
        // Lo apagamos suavemente
        await worker.StopAsync(CancellationToken.None);

        // 3. ASSERT
        mockStorage.Received().MarkJobAsCompleted(
            jobId: jobDefinition.Id, 
            intervalSeconds: 60, 
            success: false, 
            errorMessage: "¡Bum!"
        );
    }
}