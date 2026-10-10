using DigitalBrain.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace DigitalBrain.Testing;

public interface ITestTarget
{
    Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken);
}

public sealed record TestBrainPlan(IReadOnlyList<ModuleDefinition> Modules, IReadOnlyDictionary<string, string?> Configuration,
    Action<IServiceCollection>? Services, TestExecutionOptions Execution, Action<IClientBuilder>? Client,
    // Sweep-period ConfigureSilo/ConfigureClient: run on the composed silo, after the server's registrations (deleted in Task 20).
    Action<Orleans.Hosting.ISiloBuilder>? Silo = null);
