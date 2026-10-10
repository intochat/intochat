using DigitalBrain.Platform.Parts;

namespace DigitalBrain.OS;

// A host that names a machine role runs its cluster storage on the part log; without one (unit and E2E
// hosts) the silo keeps the cluster storage its server composition registered.
public static class OsMachine
{
    public static IHostApplicationBuilder AddMachinePartLogWhenConfigured(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(builder.Configuration[$"{PartsOptions.Key}:{nameof(PartsOptions.Role)}"])) { return builder; }
        builder.UseOrleans(silo => silo.AddMachinePartLog());
        return builder;
    }
}
