using Microsoft.Extensions.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Time;

public sealed class TimeModule() : Module<TimeModuleOptions>(new ModuleId(ModuleName))
{
    internal const string ModuleName = "time";

    public override System.Reflection.Assembly? Contracts => typeof(IntoChat.OS.Modules.Time.Reminders.IReminder).Assembly;

    public static readonly string MinimumReminderPeriodKey =
        IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId(ModuleName)) + ":MinimumReminderPeriod";

    // R4-04: what the module needs from its host. The timer and the reminder are plain neurons (TimeNeurons); the topology
    // must bind durable reminders, which replaces the old start-time check for an Orleans reminder provider.
    public static DigitalBrain.ModuleManifest Manifest => TimeNeurons.Manifest;

    public override Type? Neurons => typeof(TimeNeurons);

    // Transitional until Configure leaves with the Framework (06-08): test and local development may accelerate reminders;
    // production keeps the host default. The runtime's host grains register reminders under these options.
    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.AddOptions<global::Orleans.Hosting.ReminderOptions>().Configure<IOptions<TimeModuleOptions>>((options, module) =>
        {
            if (module.Value.MinimumReminderPeriod is { } period && period > TimeSpan.Zero) { options.MinimumReminderPeriod = period; }
        });
}

// The DigitalBrain module of Time (06-03): its assembly's [Neuron] classes (TimerNeuron, ReminderNeuron) run on the runtime's
// host grains, on TestOS and on the silo alike.
[DigitalBrain.Module(TimeModule.ModuleName)]
public sealed class TimeNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [DigitalBrain.Need.Reminders],
        Settings: [DigitalBrain.Setting.Of<TimeModuleOptions>(IntoChatOSConfiguration.ModuleOptionsSection(new ModuleId(TimeModule.ModuleName)))],
        Secrets: []);
}
