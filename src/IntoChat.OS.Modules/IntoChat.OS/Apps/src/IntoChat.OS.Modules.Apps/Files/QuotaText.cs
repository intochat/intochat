using System.Globalization;
using DigitalBrain.Capabilities.Storage;

namespace IntoChat.Files;

// Tri-state quota. Unknown and a known total of 0 never render as 0.
public static class QuotaText
{
    public static string For(Quota quota, string kind)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (quota.State == QuotaState.Unknown || quota.State == QuotaState.Known && quota.Total == 0)
        {
            return FilesCopy.SpaceUnknown;
        }
        if (quota.State == QuotaState.Unlimited)
        {
            return Format(quota.Used ?? 0) + " used";
        }
        var used = quota.Used ?? 0;
        var total = quota.Total ?? 0;
        return IsLocal(kind)
            ? Format(total - used) + " free of " + Format(total)
            : Format(used) + " of " + Format(total);
    }

    public static bool IsLocal(string kind)
        => kind.Equals("local", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("windows", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("disk", StringComparison.OrdinalIgnoreCase);

    public static string Format(long bytes)
    {
        const decimal gb = 1024m * 1024m * 1024m;
        const decimal mb = 1024m * 1024m;
        const decimal kb = 1024m;
        var (value, unit) = bytes >= gb ? (bytes / gb, "GB") : bytes >= mb ? (bytes / mb, "MB") : (bytes / kb, "KB");
        return decimal.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture) + " " + unit;
    }
}
