namespace IntoChat.OS.Framework.Modules;

/// <summary>Declares the product a module is licensed as.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresLicenseAttribute(string product) : Attribute
{
    public string Product { get; } = product;
}
