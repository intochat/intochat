using DigitalBrain.Testing;
using IntoChat.OS.Apps;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Web;

namespace IntoChat.OS.Tests.Integration.Journeys;

/// <summary>
/// Entry point for vertical-slice journey tests running on both <see cref="NeuronRuntimeKind.TestOS"/>
/// and <see cref="NeuronRuntimeKind.Silo"/>.
/// </summary>
public static class Journey
{
    /// <summary>The vertical-slice OS modules hosted by journey tests.</summary>
    public static readonly Type[] SliceModules =
    [
        typeof(AIModule),
        typeof(AppsModule),
        typeof(PostgresModule),
        typeof(WebModule),
        typeof(IdentityModule),
        typeof(ShippedShellAppsModule),
    ];

    /// <summary>Starts a <see cref="JourneyOS"/> with <see cref="SliceModules"/> and the configured edge fakes.</summary>
    public static Task<JourneyOS> Start(NeuronRuntimeKind kind, Action<FakeSet> fakes, CancellationToken ct = default)
        => JourneyOS.Start(kind, SliceModules, fakes, ct);
}
