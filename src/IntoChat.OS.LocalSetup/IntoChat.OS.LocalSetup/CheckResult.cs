using System.Globalization;

namespace IntoChat.OS.LocalSetup;

// Pass: ready. Warn: works but degraded or will be fixed by the launcher (exit stays 0). Fail: cannot start (exit 1).
public enum CheckStatus { Pass, Warn, Fail }

public sealed record CheckResult(string Id, CheckStatus Status, string Message, string? Fix = null)
{
    public static CheckResult Pass(string id, string message) => new(id, CheckStatus.Pass, message);

    public static CheckResult Warn(string id, string message, string fix) => new(id, CheckStatus.Warn, message, fix);

    public static CheckResult Fail(string id, string message, string fix) => new(id, CheckStatus.Fail, message, fix);
}

internal static class SizeText
{
    private const double Gib = 1024d * 1024 * 1024;

    public static string Gibibytes(long bytes) => (bytes / Gib).ToString("F1", CultureInfo.InvariantCulture) + " GiB";

    public static string Humanize(long bytes)
        => bytes >= Gib
            ? (bytes / Gib).ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024d * 1024)).ToString("F0", CultureInfo.InvariantCulture) + " MB";
}
