global using ITimer = DigitalBrain.Time.Timers.ITimer;
using DigitalBrain.Time;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Timers;

namespace DigitalBrain.Modules.Time.Tests.Integration;

internal static class TimeTestControls
{
    // Rule 4 seam: the clock and timer delivery have no integration boundary, so the test owns them in-process.
    // These decorate Orleans' own registries; a test's ConfigureServices runs after Orleans registered them.
    public static void UseControlledTimers(this IServiceCollection services, ControlledTimers timers)
    {
        Decorate<ITimerRegistry>(services, inner => new ControlledTimerRegistry(inner, timers));
        services.AddKeyedSingleton<TimeProvider>(typeof(TimeModule), timers);
    }

    // Rule 4 seam: a time knob, so idle collection happens within a fact.
    public static void UseFastCollection(this IServiceCollection services)
        => services.Configure<GrainCollectionOptions>(options =>
        {
            options.CollectionAge = TimeSpan.FromMilliseconds(200);
            options.CollectionQuantum = TimeSpan.FromMilliseconds(100);
        });

    // Rule 4 seam: reminder-storage fault injection and an accelerated period (a test setting, never a production default).
    public static void UseReminderControl(this IServiceCollection services, ReminderControl reminders)
    {
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
