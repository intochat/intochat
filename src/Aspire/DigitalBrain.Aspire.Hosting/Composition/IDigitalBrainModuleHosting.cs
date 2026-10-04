using DigitalBrain.Contracts;

namespace DigitalBrain.Aspire.Hosting;

public interface IDigitalBrainModuleHosting
{
    ModuleId Module { get; }
    void Configure(DigitalBrainBuilder brain);
}
