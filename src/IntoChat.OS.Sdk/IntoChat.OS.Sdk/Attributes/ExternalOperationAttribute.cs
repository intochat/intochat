namespace IntoChat.OS.Sdk;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ExternalOperationAttribute(string name, string integration, string unit) : Attribute
{
    public string Name { get; } = name;
    public string Integration { get; } = integration;
    public string Unit { get; } = unit;
}
