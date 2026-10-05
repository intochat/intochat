using DigitalBrain;

namespace DigitalBrain.Microsoft.CSharp;

[PlatformOnly]
public interface ICSharpAppBinding : INeuron
{
    Task BindApp(string appId);
}
