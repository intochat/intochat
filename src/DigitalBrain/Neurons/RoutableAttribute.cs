namespace DigitalBrain;

// A contract the router may choose from: a person-facing capability Jev can carry a request to.
// Model markers, the AI runtime (ISystemOne, IAgent, IModelCatalog, IRouter), identity, integration
// and tool infrastructure are left unmarked, so Jev never routes to them.
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class RoutableAttribute : Attribute
{
    public static bool AppliesTo(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return contract.IsDefined(typeof(RoutableAttribute), inherit: false);
    }
}