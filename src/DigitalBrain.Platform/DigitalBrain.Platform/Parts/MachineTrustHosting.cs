using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Platform.Parts;

public static class MachineTrustHosting
{
    // What a machine adds for handing parts to other machines of its trust cluster (Task 18): releasing a part
    // deactivates its grains here first. The trust clients and machine registration come with the platform.
    public static ISiloBuilder AddMachineHandover(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton<PartActivations>();
        return silo;
    }

    // Task 22: on a machine of a separate trust cluster a call for a part held elsewhere runs at its holder, and this
    // machine answers the calls other machines forward to it. Registered after the kernel's filters, so the trace and
    // caller are on the RequestContext it forwards.
    internal static ISiloBuilder AddCrossPartCalls(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        // Machine links need QUIC; AddMachineLinks already refuses any other platform.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) { CrossPartCalls(silo); }
        return silo;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void CrossPartCalls(ISiloBuilder silo)
    {
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(PartRouter))) { return; }
        services.TryAddSingleton<PartRoutes>();
        services.TryAddSingleton<PartRouter>();
        services.TryAddSingleton<DigitalBrain.Framework.IStandInActivations>(provider => provider.GetRequiredService<PartRouter>());
        services.TryAddSingleton<ForwardedCallEndpoint>();
        services.TryAddSingleton<PartForwarder>();
        services.AddSingleton<IOutgoingGrainCallFilter, PartCallFilter>();
        services.AddSingleton<IIncomingGrainCallFilter, PartIncomingCallFilter>();
        silo.AddStartupTask((provider, _) =>
        {
            var endpoint = provider.GetRequiredService<ForwardedCallEndpoint>();
            provider.GetRequiredService<Links.QuicMachineTransport>().Handle(PartCallFilter.Operation, endpoint.Answer);
            return Task.CompletedTask;
        }, ServiceLifecycleStage.RuntimeGrainServices);
    }
}
