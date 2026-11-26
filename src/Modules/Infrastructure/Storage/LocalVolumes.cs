using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.LocalDisk;

/// <summary>One volume row. Tests supply fakes; the host reads <see cref="DriveInfo"/>.</summary>
internal sealed record VolumeRow(string Name, string DriveFormat, bool IsReady, DriveType DriveType, long AvailableFreeSpace, long TotalSize);

internal interface IVolumeSource
{
    IReadOnlyList<VolumeRow> Drives();

    VolumeRow? ForPath(string path);
}

internal static class LocalVolumes
{
    public static readonly IReadOnlyList<string> ExcludedFormats =
    [
        "proc", "sysfs", "tmpfs", "devtmpfs", "overlay", "cgroup", "cgroup2", "devpts", "mqueue",
        "securityfs", "debugfs", "tracefs", "fusectl", "configfs", "pstore", "bpf", "autofs", "squashfs", "nsfs",
    ];

    private static readonly HashSet<string> ExcludedFormatSet = new(ExcludedFormats, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ExcludedMacVolumes =
    [
        "/System/Volumes/VM", "/System/Volumes/Preboot", "/System/Volumes/Update",
        "/System/Volumes/xarts", "/System/Volumes/iSCPreboot", "/System/Volumes/Hardware",
    ];

    public static bool Include(VolumeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (ExcludedFormatSet.Contains(row.DriveFormat)) { return false; }
        var name = row.Name.Replace('\\', '/').TrimEnd('/');
        return !ExcludedMacVolumes.Contains(name, StringComparer.Ordinal);
    }

    // Every distinct volume the roots are on, summed; unknown when any of them is unknown.
    public static Quota QuotaFor(IReadOnlyList<string> roots, IVolumeSource source)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(source);
        if (roots.Count == 0) { return Quota.Unknown; }
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long used = 0, total = 0;
        foreach (var root in roots)
        {
            VolumeRow? volume;
            try { volume = source.ForPath(root); }
            catch (IOException) { return Quota.Unknown; }
            catch (UnauthorizedAccessException) { return Quota.Unknown; }
            if (volume is null) { return Quota.Unknown; }
            if (!seen.Add(volume.Name)) { continue; }
            var one = QuotaFor(root, source);
            if (one.State != QuotaState.Known || one.Used is not { } u || one.Total is not { } t) { return Quota.Unknown; }
            try { checked { used += u; total += t; } }
            catch (OverflowException) { return Quota.Unknown; }
        }
        return Quota.Known(used, total);
    }

    public static Quota QuotaFor(string root, IVolumeSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var volume = source.ForPath(root);
            if (volume is null || !Include(volume) || !volume.IsReady || volume.DriveType == DriveType.Network) { return Quota.Unknown; }
            if (volume.TotalSize <= 0 || volume.AvailableFreeSpace < 0 || volume.AvailableFreeSpace > volume.TotalSize) { return Quota.Unknown; }
            return Quota.Known(volume.TotalSize - volume.AvailableFreeSpace, volume.TotalSize);
        }
        catch (IOException) { return Quota.Unknown; }
        catch (UnauthorizedAccessException) { return Quota.Unknown; }
    }
}

internal sealed class DriveVolumeSource : IVolumeSource
{
    internal static DriveVolumeSource Instance { get; } = new();

    public IReadOnlyList<VolumeRow> Drives()
    {
        var rows = new List<VolumeRow>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return rows; }
        catch (UnauthorizedAccessException) { return rows; }
        foreach (var drive in drives)
        {
            try
            {
                var ready = drive.IsReady;
                rows.Add(new VolumeRow(
                    drive.Name,
                    ready ? drive.DriveFormat : "",
                    ready,
                    drive.DriveType,
                    ready ? drive.AvailableFreeSpace : 0,
                    ready ? drive.TotalSize : 0));
            }
            catch (IOException) { rows.Add(new VolumeRow(drive.Name, "", false, DriveType.Unknown, 0, 0)); }
            catch (UnauthorizedAccessException) { rows.Add(new VolumeRow(drive.Name, "", false, DriveType.Unknown, 0, 0)); }
        }
        return rows;
    }

    public VolumeRow? ForPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            OsConsoleLog.For(typeof(LocalVolumes)).LogSwallowed(error, "resolving local volume path");
            return null;
        }
        VolumeRow? best = null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var drive in Drives().Where(LocalVolumes.Include))
        {
            if (drive.Name.Length == 0 || !full.StartsWith(drive.Name, comparison)) { continue; }
            if (best is null || drive.Name.Length > best.Name.Length) { best = drive; }
        }
        return best;
    }
}
