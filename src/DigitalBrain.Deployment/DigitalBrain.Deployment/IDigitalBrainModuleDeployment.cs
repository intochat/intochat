using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Deployment;

// A module's cloud deployment, claiming its module through the generic parameter. It asks the
// DeploymentKit foundation for what it needs, then deploys the Azure resources behind the manifest
// resources it owns and provides the values the brain runtime's environment reads from them.
public interface IDigitalBrainModuleDeployment
{
    ModuleId Module { get; }

    void ConfigureFoundation(FoundationContext context) { }

    void Deploy(ModuleDeploymentContext context);
}

public abstract class ModuleDeployment<TModule> : IDigitalBrainModuleDeployment where TModule : Module, new()
{
    public ModuleId Module { get; } = new TModule().Id;
    public abstract void Deploy(ModuleDeploymentContext context);
}
