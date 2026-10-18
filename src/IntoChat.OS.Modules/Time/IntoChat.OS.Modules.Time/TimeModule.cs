using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Time.Reminders;
using IntoChat.OS.Time.Timers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace IntoChat.OS.Time;

public sealed class TimeModule() : Module<TimeModuleOptions>(new ModuleId("time"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Time.Reminders.IReminder).Assembly;

    public static readonly string MinimumReminderPeriodKey =
        IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId("time")) + ":MinimumReminderPeriod";

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
