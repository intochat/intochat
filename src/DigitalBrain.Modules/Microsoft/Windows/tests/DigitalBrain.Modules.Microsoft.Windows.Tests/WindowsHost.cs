using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

internal static class WindowsHost
{
    private const string Principal = "acct";
    private const string BrainName = "home";
    private static readonly string Scope = BrainScope.Create(Principal, BrainName).Id;

    public static string Machine(string machineId) => ConnectionKeys.For(Scope, "windows", machineId);

    // The caller context is async-local: stamped here, outside the async body, it flows back to the fact.
    public static Task<(ModuleBrain Host, string MachineId, string Root)> StartAsync(CancellationToken ct)
    {
        CallerContextStamper.Stamp(new()
        { PrincipalId = Principal, AccountId = Principal, BrainId = BrainName, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        return StartHostAsync(ct);
    }

    private static async Task<(ModuleBrain Host, string MachineId, string Root)> StartHostAsync(CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("windows-facts").FullName;
        var idFile = Path.Combine(temp, "machine-id");
        var machineId = Guid.NewGuid().ToString("N");
        File.WriteAllText(idFile, machineId);
        var root = Directory.CreateDirectory(Path.Combine(temp, "root")).FullName;

        var host = await ModuleTest.Create()
            .WithModule<WindowsModule>()
            .WithOptions<WindowsModule, WindowsOptions>(options => options.MachineIdFile = idFile)
            .StartAsync(ct);
        await host.AuthorizeCallerAsync();
        return (host, machineId, root);
    }
}
