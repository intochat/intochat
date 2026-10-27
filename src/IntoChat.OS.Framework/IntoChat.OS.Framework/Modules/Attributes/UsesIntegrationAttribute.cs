namespace IntoChat.OS.Framework.Modules;

/// <summary>Names an integration another module declares, so this module's external operations can bill against it; repeatable.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesIntegrationAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
