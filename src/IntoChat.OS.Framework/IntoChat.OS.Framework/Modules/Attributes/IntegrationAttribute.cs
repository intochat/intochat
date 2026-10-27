namespace IntoChat.OS.Framework.Modules;

/// <summary>Declares an integration (credential and setting bundle) a module needs; repeatable.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class IntegrationAttribute(string id, string displayName) : Attribute
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
}
