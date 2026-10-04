namespace DigitalBrain.Kernel;

// Names a module's Aspire hosting adapter without the module depending on the hosting stack.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ModuleHostingAttribute(string typeName) : Attribute
{
    public string TypeName { get; } = typeName;
}
