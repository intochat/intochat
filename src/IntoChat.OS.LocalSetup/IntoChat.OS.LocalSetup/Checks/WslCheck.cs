using System.Text;

namespace IntoChat.OS.LocalSetup.Checks;

// Soft check: Docker Desktop can run on Hyper-V, so a missing WSL is a warning.
public sealed class WslCheck : IPreflightCheck
{
    public const string Id = "wsl";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        if (!context.Environment.IsWindows) { return []; }
        var result = await context.Environment.RunAsync("wsl", ["-l", "-v"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return [CheckResult.Warn(Id, "WSL is not available.", "Run 'wsl --install' unless Docker Desktop uses Hyper-V.")];
        }
        var distros = ParseDistros(result.Stdout);
        return distros.Count == 0
            ? [CheckResult.Warn(Id, "WSL has no distributions.", "Run 'wsl --install' unless Docker Desktop uses Hyper-V.")]
            : [CheckResult.Pass(Id, "WSL distributions: " + string.Join(", ", distros) + ".")];
    }

    // `wsl -l -v` writes UTF-16LE: decode it as such before splitting columns (NAME STATE VERSION, default marked '*').
    public static IReadOnlyList<string> ParseDistros(byte[] stdout)
    {
        var text = Encoding.Unicode.GetString(stdout).Replace("\0", string.Empty, StringComparison.Ordinal).Trim('﻿');
        var distros = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Trim().TrimStart('*').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 3 || columns[0].Equals("NAME", StringComparison.OrdinalIgnoreCase)) { continue; }
            distros.Add(columns[0]);
        }
        return distros;
    }
}
