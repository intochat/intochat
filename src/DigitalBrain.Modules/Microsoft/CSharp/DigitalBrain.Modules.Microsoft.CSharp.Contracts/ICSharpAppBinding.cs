namespace DigitalBrain.Microsoft.CSharp;

[Alias("microsoft.csharp.app-binding")]
public interface ICSharpAppBinding : INeuron
{
    Task BindApp(string appId);
}
