using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Testing;

public interface ITestTarget
{
    Task<ITestBrain> StartAsync(TestBrainPlan plan, CancellationToken cancellationToken);
}

public sealed record TestBrainPlan(IReadOnlyList<ModuleDefinition> Modules, IReadOnlyDictionary<string, string?> Configuration,
    Action<IServiceCollection>? Services, TestExecutionOptions Execution, Action<IClientBuilder>? Client, ILicenseCheck? LicenseCheck = null);
