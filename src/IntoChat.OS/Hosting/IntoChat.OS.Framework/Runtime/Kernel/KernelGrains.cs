// os-only: the os kernel type-table grain contract, which carries Feature.
using DigitalBrain;

// The type-table contract stays with the product kernel: it carries Feature, which the runtime does not.
// Instance directory keys and the compiled type table live in DigitalBrain.Runtime.Orleans.
namespace IntoChat.OS.Framework;

[Alias("kernel.type-table")]
internal interface ITypeTable : IGrainWithStringKey
{
    Task<BrainType[]> List();
    Task<Feature[]> Features();
    Task Load(Feature feature, BrainType[] types);
    Task Unload(string name);
}

