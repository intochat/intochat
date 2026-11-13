using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Time;

// Plain timer and reminder neurons in this assembly. The host must supply durable reminders.
[DigitalBrain.Modules.Module(TimeModule.ModuleName)]
public sealed class TimeModule : DigitalBrain.Modules.Module
{
    internal const string ModuleName = "time";

    public static System.Reflection.Assembly? Contracts => typeof(Reminders.IReminder).Assembly;

    public static readonly string MinimumReminderPeriodKey =
        ModuleConfiguration.ModuleOptionsSection(new ModuleId(ModuleName)) + ":MinimumReminderPeriod";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [DigitalBrain.Modules.Need.Reminders],
        Settings: [DigitalBrain.Modules.Setting.Of<TimeModuleOptions>(ModuleConfiguration.ModuleOptionsSection(new ModuleId(ModuleName)))],
        Secrets: []);
}
