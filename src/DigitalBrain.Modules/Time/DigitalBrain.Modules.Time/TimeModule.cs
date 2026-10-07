using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Timers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Time;

public sealed class TimeModule() : Module(new ModuleId("time"))
{
    public override System.Reflection.Assembly? Contracts => typeof(DigitalBrain.Time.Reminders.IReminder).Assembly;

    public static readonly string MinimumReminderPeriodKey =
        DigitalBrainConfiguration.ModuleOptionsSection(new ModuleId("time")) + ":MinimumReminderPeriod";

    public override void Configure(ISiloBuilder silo)
    {
        silo.AddTime();
        // Test and local development may accelerate reminders; production keeps the Orleans default.
        silo.Services.AddOptions<ReminderOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            if (TimeSpan.TryParse(configuration[MinimumReminderPeriodKey], out var period) && period > TimeSpan.Zero)
            { options.MinimumReminderPeriod = period; }
        });
    }
}
