using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Aspire.Hosting;

public interface IDigitalBrainModuleHosting
{
    ModuleId Module { get; }
    void Configure(DigitalBrainBuilder brain);
}
