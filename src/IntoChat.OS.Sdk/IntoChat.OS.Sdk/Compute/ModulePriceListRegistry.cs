using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Sdk.Compute;

public sealed class ModulePriceListRegistry
{
    private readonly Dictionary<string, (ModulePriceListRegistration Owner, ExternalOperationDescriptor Operation)> _operations = new(StringComparer.Ordinal);

    public ModulePriceListRegistry(IEnumerable<ModulePriceListRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            foreach (var operation in registration.Operations)
            {
                if (!_operations.TryAdd(operation.Name, (registration, operation)))
                { throw new InvalidOperationException($"External operation '{operation.Name}' has multiple owners."); }
            }
        }
    }

    public ModulePriceListRegistration Resolve(string operation) => Find(operation).Owner;
    public ExternalOperationDescriptor GetOperation(string operation) => Find(operation).Operation;

    private (ModulePriceListRegistration Owner, ExternalOperationDescriptor Operation) Find(string operation)
        => _operations.TryGetValue(operation, out var declaration)
            ? declaration
            : throw new InvalidOperationException($"External operation '{operation}' is not declared by a module.");
}
