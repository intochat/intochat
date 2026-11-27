namespace DigitalBrain.Modules;

/// <summary>
/// Marks a static partial method returning <see cref="IReadOnlyList{T}"/> of <see cref="Type"/>
/// to be implemented at compile time with every concrete public <see cref="Module"/> subclass decorated with
/// <see cref="ModuleAttribute"/> in the current assembly and its referenced assemblies.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class GeneratedModuleRegistryAttribute : Attribute;

/// <summary>
/// Assembly-level declaration emitted at compile time for each concrete public <see cref="Module"/> subclass
/// decorated with <see cref="ModuleAttribute"/> in the assembly, enabling reflection-free discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RegisteredModuleAttribute(Type moduleType) : Attribute
{
    /// <summary>The declared module type.</summary>
    public Type ModuleType { get; } = moduleType ?? throw new ArgumentNullException(nameof(moduleType));
}
