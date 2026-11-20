using DigitalBrain.Identity.Local.Machine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition.Machine;

// Every OS host has one MachineId. Its file is the IntoChatOS:Machine:IdFile setting, else the per-OS default the
// OS has always used; moving the default would give existing installs a new id and orphan what the old one addresses.
internal static class MachineHosting
{
    public const string IdFileKey = "IntoChatOS:Machine:IdFile";

    public static IServiceCollection AddMachineId(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => new MachineId(IdFile(provider.GetService<IConfiguration>()?[IdFileKey])));
        return services;
    }

    // Windows: %LOCALAPPDATA%\IntoChatOS; macOS: Application Support/IntoChatOS; Linux and containers:
    // $XDG_DATA_HOME/intochat-os, falling back to ~/.local/share/intochat-os.
    internal static string IdFile(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) { return configured; }
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntoChatOS", "machine-id");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) { return Path.Combine(home, "Library", "Application Support", "IntoChatOS", "machine-id"); }
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(data) ? Path.Combine(home, ".local", "share") : data, "intochat-os", "machine-id");
    }
}
