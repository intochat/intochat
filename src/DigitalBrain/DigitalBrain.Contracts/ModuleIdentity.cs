namespace DigitalBrain.Contracts;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ModuleIdAttribute(string id) : Attribute { public string Id { get; } = id; }

// Names a module's Aspire hosting adapter without the module depending on the hosting stack.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ModuleHostingAttribute(string typeName) : Attribute
{
    public string TypeName { get; } = typeName;
}

// Names a module's cloud deployment contributor without the module depending on the deployment stack.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ModuleDeploymentAttribute(string typeName) : Attribute
{
    public string TypeName { get; } = typeName;
}

public static class ModuleIdentity
{
    public static string Get(Type module) => module.GetCustomAttributes(typeof(ModuleIdAttribute), false)
        .Cast<ModuleIdAttribute>().SingleOrDefault()?.Id ?? module.Name;
}
