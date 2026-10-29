using IntoChat.OS.Contracts.Compute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Framework.Modules.Metering;

public static class ModulePriceLists
{
    public static IServiceCollection AddModulePriceList<TPriceList>(this IServiceCollection services,
        string moduleId, ExternalOperationDescriptor[] operations)
        where TPriceList : class, IPriceList
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(operations);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ValidateToken(operation.Name, nameof(operations));
            ValidateToken(operation.Integration, nameof(operations));
            ValidateToken(operation.Unit, nameof(operations));
            if (!names.Add(operation.Name))
            { throw new InvalidOperationException($"External operation '{operation.Name}' is declared more than once."); }
        }
        var declared = operations.OrderBy(operation => operation.Name, StringComparer.Ordinal).ToArray();

        var registrations = services.Where(descriptor => descriptor.ServiceType == typeof(ModulePriceListRegistration))
            .Select(descriptor => descriptor.ImplementationInstance as ModulePriceListRegistration
                ?? throw new InvalidOperationException("Module price lists must be registered with AddModulePriceList."))
            .ToArray();
        foreach (var registration in registrations)
        {
            if (registration.ModuleId == moduleId)
            {
                if (registration.PriceListType == typeof(TPriceList) && registration.Operations.SequenceEqual(declared))
                { return services; }
                throw new InvalidOperationException($"Module '{moduleId}' has conflicting price-list declarations.");
            }
            if (registration.Operations.Any(operation => names.Contains(operation.Name)))
            { throw new InvalidOperationException("External operation ownership overlaps between modules."); }
        }

        if (services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(TPriceList)
            && descriptor.Lifetime != ServiceLifetime.Singleton))
        { throw new InvalidOperationException($"Module price list '{typeof(TPriceList).Name}' must be registered as a singleton."); }

        if (!registrations.Any(registration => registration.PriceListType == typeof(TPriceList))
            && services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IPriceList)
                && (descriptor.ImplementationType == typeof(TPriceList)
                    || descriptor.ImplementationInstance is TPriceList
                    || descriptor.ImplementationFactory?.GetType().GenericTypeArguments.LastOrDefault() == typeof(TPriceList))))
        { throw new InvalidOperationException($"Register '{typeof(TPriceList).Name}' through AddModulePriceList so concrete and IPriceList resolution share one singleton."); }

        services.TryAddSingleton<TPriceList>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPriceList, TPriceList>(provider => provider.GetRequiredService<TPriceList>()));
        services.AddSingleton(new ModulePriceListRegistration(moduleId, typeof(TPriceList), declared));
        services.TryAddSingleton<ModulePriceListRegistry>();
        return services;
    }

    private static void ValidateToken(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '/'))
        { throw new ArgumentException("Operation names, integrations and units must be canonical tokens of at most 128 characters.", parameter); }
    }
}
