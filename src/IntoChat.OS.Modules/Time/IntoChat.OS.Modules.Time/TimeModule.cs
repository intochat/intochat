using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Time.Timers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Time;

public sealed class TimeModule() : Module<TimeModuleOptions>(new ModuleId("time"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Time.Reminders.IReminder).Assembly;

    public static readonly string MinimumReminderPeriodKey =
        IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId("time")) + ":MinimumReminderPeriod";

    // R4-04: what the module needs from its host. Durable reminders are a topology binding; the options bind from the
    // module's section. Configure below keeps the registrations until the timer and reminder become plain neurons.
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [DigitalBrain.Need.Reminders],
        Settings: [DigitalBrain.Setting.Of<TimeModuleOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId("time")))],
        Secrets: []);

    public override void Configure(ISiloBuilder silo)
    {
        silo.AddTime();
        // Test and local development may accelerate reminders; production keeps the Orleans default.
        silo.Services.AddOptions<ReminderOptions>().Configure<IOptions<TimeModuleOptions>>((options, module) =>
        {
            if (module.Value.MinimumReminderPeriod is { } period && period > TimeSpan.Zero) { options.MinimumReminderPeriod = period; }
        });
    }
}
