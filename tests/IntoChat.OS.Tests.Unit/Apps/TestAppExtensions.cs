using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Screens;
using DigitalBrain.Testing;

namespace IntoChat.OS.Apps.Tests.Unit;

public sealed class InstalledApp<TApp> where TApp : Neuron
{
    private readonly TestOS _os;
    private readonly IScreen _screen;
    private readonly TApp _app;

    public InstalledApp(TestOS os, IScreen screen, TApp app)
    {
        _os = os;
        _screen = screen;
        _app = app;
    }

    public TestOS OS => _os;
    public TApp App => _app;
    public IScreen Screen => _screen;

    public PresentedView Main
    {
        get
        {
            var published = _screen.Read().GetAwaiter().GetResult();
            return published is not null ? PresentedView.From(published) : new PresentedView("main", null);
        }
    }

    public PresentedView Side
    {
        get
        {
            if (_app is IntoChat.OS.Apps.Ino.InoApp ino)
            {
                return new PresentedView("side", ino.RenderSide());
            }
            if (_app is IntoChat.OS.Apps.Tasks.TasksApp tasks)
            {
                return new PresentedView("side", tasks.RenderSide());
            }
            var published = _screen.Read().GetAwaiter().GetResult();
            return published is not null ? PresentedView.From(published) : new PresentedView("side", null);
        }
    }

    private static string ResolveSignalAlias(Type appType, string operation)
    {
        foreach (var method in appType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.GetCustomAttribute<OnAttribute>() is null)
            {
                continue;
            }
            var paramType = method.GetParameters().FirstOrDefault()?.ParameterType;
            if (paramType is null || !typeof(Signal).IsAssignableFrom(paramType))
            {
                continue;
            }

            var alias = paramType.GetCustomAttribute<SignalAttribute>()?.Alias ?? paramType.Name;
            if (string.Equals(method.Name, operation, StringComparison.OrdinalIgnoreCase)
                || string.Equals(alias, operation, StringComparison.OrdinalIgnoreCase)
                || alias.EndsWith("." + operation, StringComparison.OrdinalIgnoreCase))
            {
                return alias;
            }
        }
        return operation;
    }

    public async Task Request(string operation, object? input = null)
    {
        var inputType = input?.GetType();
        var signalAlias = inputType?.GetCustomAttribute<SignalAttribute>()?.Alias
            ?? ResolveSignalAlias(typeof(TApp), operation);
        var json = input is not null
            ? JsonSerializer.SerializeToElement(input, RuntimeProtocol.OptionsFor(inputType!))
            : (JsonElement?)null;
        await _screen.Request(signalAlias, json);
        await _os.Idle();
    }
}

public static class TestOsAppExtensions
{
    public static async Task<InstalledApp<TApp>> Install<TApp>(this TestOS os) where TApp : Neuron, IScreen
    {
        os.Register<TApp>();
        var alias = PlainNeuronContracts.TypeOf(typeof(TApp))
            ?? typeof(TApp).GetCustomAttribute<NeuronAttribute>()?.Type
            ?? typeof(TApp).Name;
        var id = new NeuronId(new BrainId("local"), alias, "default");
        TApp? app = null;
        using (IntoChat.OS.Apps.AppActivationScope.Capture(neuron =>
        {
            if (neuron is TApp typed)
            {
                app = typed;
            }
        }))
        {
            await os.Activate(id);
        }
        await os.Idle();
        var screen = os.Get<IScreen>(id);
        if (app is null)
        {
            throw new InvalidOperationException($"App instance was not captured during activation of {id}.");
        }

        return new InstalledApp<TApp>(os, screen, app);
    }
}
