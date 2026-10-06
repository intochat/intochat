namespace DigitalBrain.Microsoft.CSharp;

public interface ICSharpAppBinding : INeuron
{
    Task BindApp(string appId);
}
