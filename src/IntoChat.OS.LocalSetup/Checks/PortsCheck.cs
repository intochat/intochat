using System.Globalization;
using System.Text.RegularExpressions;

namespace IntoChat.OS.LocalSetup.Checks;

// The fixed ports the AppHost binds on 127.0.0.1 (fronts, dashboard, Orleans endpoints), plus the shell's when it is on.
public sealed partial class PortsCheck : IPreflightCheck
{
    public const string Id = "ports";
    public static readonly IReadOnlyList<int> FixedPorts = [5080, 5081, 15100, 17197, 21123, 21124, 22146];
    public static readonly IReadOnlyList<int> ShellPorts = [54721, 54722, 54723];

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s*\*?\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Range();

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        var excluded = new List<(int Start, int End)>();
        if (context.Environment.IsWindows)
        {
            var netsh = await context.Environment.RunAsync("netsh", ["int", "ipv4", "show", "excludedportrange", "protocol=tcp"], cancellationToken).ConfigureAwait(false);
            if (netsh.ExitCode == 0)
            {
                foreach (Match match in Range().Matches(netsh.StdoutText))
                {
                    excluded.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
                }
            }
        }

        var ports = context.Options.ShellEnabled ? [.. FixedPorts, .. ShellPorts] : FixedPorts;
        var results = new List<CheckResult>();
        foreach (var port in ports)
        {
            if (excluded.Any(range => port >= range.Start && port <= range.End))
            {
                results.Add(CheckResult.Fail($"port-{port}", $"port {port} is excluded by Windows.", $"Release the reserved range (netsh int ipv4 show excludedportrange protocol=tcp) or reboot; port {port} is fixed."));
            }
            else if (!context.Environment.CanBind(port))
            {
                results.Add(CheckResult.Fail($"port-{port}", $"port {port} is in use.", $"Stop the process using port {port} (netstat -ano | findstr :{port})."));
            }
        }
        return results.Count == 0 ? [CheckResult.Pass(Id, $"{ports.Count} fixed ports are free.")] : results;
    }
}
