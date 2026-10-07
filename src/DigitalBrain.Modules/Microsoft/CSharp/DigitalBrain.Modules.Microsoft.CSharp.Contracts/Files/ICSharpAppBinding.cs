namespace DigitalBrain.Microsoft.CSharp.Files;

[Alias("microsoft.csharp.app-binding")]
public interface ICSharpAppBinding : INeuron
{
    Task BindApp(string appId);
}
