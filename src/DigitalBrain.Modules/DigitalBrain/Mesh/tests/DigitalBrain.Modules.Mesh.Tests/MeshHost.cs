using DigitalBrain.Framework;
using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

internal static class MeshHost
{
    private const string Principal = "acct";
    private const string BrainName = "home";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;

    public static string Key(string name) => $"{Scope}/mesh/{name}";

    public static async Task<ModuleBrain> StartAsync(CancellationToken ct, Action<MeshOptions>? configure = null)
    {
        var host = await ModuleTest.Create()
            .WithModule<MeshModule>()
            .ConfigureModule<MeshModule, MeshOptions>(options => configure?.Invoke(options))
            .StartAsync(ct);
        CallerContextStamper.Stamp(new()
        { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        await host.AuthorizeCallerAsync();
        return host;
    }
}
