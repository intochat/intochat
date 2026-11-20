using DigitalBrain;
using DigitalBrain.Kernel;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Host.Kernel;

// Type entries a composed module declares outside its contract assemblies, such as its connectors. CompiledTypes adds
// them after the module's neurons and signals; a host without a source compiles neurons and signals alone.
public interface IModuleTypeSource
{
    IEnumerable<BrainType> TypesOf(OsModule module);
}
