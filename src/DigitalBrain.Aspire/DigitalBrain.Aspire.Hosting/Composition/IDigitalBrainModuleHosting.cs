using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Aspire.Hosting;

public interface IDigitalBrainModuleHosting
{
    ModuleId Module { get; }
    void Configure(DigitalBrainBuilder brain);
}
