global using ITimer = DigitalBrain.Time.Timers.ITimer;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Timers;

namespace DigitalBrain.Modules.Time.Tests;

internal static class TimeTestControls
{
    // Rule 4 seam: the clock and timer delivery have no integration boundary, so the test owns them in-process.
    // These decorate Orleans' own registries, so they run on the silo builder, after Orleans registered them.
    public static void UseControlledTimers(this ISiloBuilder silo, ControlledTimers timers)
    {
        var services = silo.Services;
        Decorate<ITimerRegistry>(services, inner => new ControlledTimerRegistry(inner, timers));
        services.AddSingleton<TimeProvider>(timers);
    }

    // Rule 4 seam: a time knob, so idle collection happens within a fact.
    public static void UseFastCollection(this IServiceCollection services)
        => services.Configure<GrainCollectionOptions>(options =>
        {
            options.CollectionAge = TimeSpan.FromMilliseconds(200);
            options.CollectionQuantum = TimeSpan.FromMilliseconds(100);
        });

    // Rule 4 seam: reminder-storage fault injection and an accelerated period (a test setting, never a production default).
    public static void UseReminderControl(this ISiloBuilder silo, ReminderControl reminders)
    {
        var services = silo.Services;
        services.Configure<ReminderOptions>(options => options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100));
        Decorate<IReminderRegistry>(services, inner => new FaultingReminders(inner, reminders));
        services.AddSingleton<IIncomingGrainCallFilter>(services =>
        {
            reminders.Table = services.GetRequiredService<IReminderTable>();
            return reminders;
        });
    }

    private static void Decorate<T>(IServiceCollection services, Func<T, T> decorate) where T : class
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(descriptor);
        services.AddSingleton<T>(provider => decorate((T)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!))));
    }
}
