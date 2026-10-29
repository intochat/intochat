namespace IntoChat.OS.Framework.Modules;

/// <summary>Links a hosting class to the module type it hosts.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ModuleHostingAttribute(Type module) : Attribute
{
    public Type Module { get; } = module;
}
