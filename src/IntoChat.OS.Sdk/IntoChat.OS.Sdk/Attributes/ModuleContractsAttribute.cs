namespace IntoChat.OS.Sdk;

/// <summary>Names a type whose assembly holds the module's exported neurons and signals.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModuleContractsAttribute(Type contractAnchor) : Attribute
{
    public Type ContractAnchor { get; } = contractAnchor;
}
