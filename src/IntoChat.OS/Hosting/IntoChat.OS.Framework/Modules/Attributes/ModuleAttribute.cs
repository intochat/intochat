namespace IntoChat.OS.Framework.Modules;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModuleAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
