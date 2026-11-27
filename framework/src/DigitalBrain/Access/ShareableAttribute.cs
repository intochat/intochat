namespace DigitalBrain.Access;

/// <summary>Marks a neuron contract interface or mini-app screen class as eligible for cross-brain sharing, capability grants, or room pinning.</summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ShareableAttribute : Attribute;
