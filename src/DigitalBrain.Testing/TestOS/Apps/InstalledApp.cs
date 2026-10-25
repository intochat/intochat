using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace DigitalBrain.Testing;

/// <summary>
/// An app installed into a <see cref="TestOS"/>: requests its operations one at a time, as turns, and reads what it presents.
/// An operation is an <c>[On]</c> method of the app; it is requested by its method name, case-insensitive, with dashes ignored.
/// </summary>
/// <typeparam name="TApp">The <c>[App]</c> class.</typeparam>
public sealed partial class InstalledApp<TApp> where TApp : App
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TestOS _os;
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private readonly Dictionary<string, MethodInfo> _operations;
    private TApp? _app;

    internal InstalledApp(TestOS os, AppManifest manifest)
    {
        _os = os;
        Manifest = manifest;
        _operations = typeof(TApp).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.IsDefined(typeof(OnAttribute), inherit: false))
            .ToDictionary(method => Key(method.Name), StringComparer.Ordinal);
    }

    /// <summary>The manifest the app was installed with.</summary>
    public AppManifest Manifest { get; }

    /// <summary>The app instance.</summary>
    public TApp App => _app ?? throw new InvalidOperationException($"App '{Manifest.Id}' is not started.");

    /// <summary>The operations the app offers: the names of its <c>[On]</c> methods, as requested by <see cref="Request(string, object?)"/>.</summary>
    public IReadOnlyList<string> Operations => [.. _operations.Values.Select(method => method.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Runs the operation <paramref name="operation"/> as a turn and completes when it has. A null <paramref name="argument"/> is the empty input:
    /// an empty string, a default value, or an object read from <c>{}</c>. Any other argument is passed as is when it fits, else converted through JSON.
    /// </summary>
    /// <exception cref="ArgumentException">The app has no such operation.</exception>
    public Task Request(string operation, object? argument = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (!_operations.TryGetValue(Key(operation), out var method))
        {
            throw new ArgumentException(
                $"App '{Manifest.Id}' has no operation '{operation}'. Operations: {string.Join(", ", Operations)}.", nameof(operation));
        }
        return _os.Track(Turn(method, argument));
    }

    internal Task Start() => _os.Track(Turn(null, null));

    // Turns run one at a time, in request order.
    private Task Turn(MethodInfo? method, object? argument)
    {
        lock (_gate)
        {
            var turn = RunAfter(_tail, method, argument);
            _tail = turn.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return turn;
        }
    }

    private async Task RunAfter(Task previous, MethodInfo? method, object? argument)
    {
        await previous.ConfigureAwait(false);
        await Task.Yield();
        _app ??= Construct();
        try
        {
            if (method is not null)
            {
                await Invoke(method, argument).ConfigureAwait(false);
            }
        }
        finally
        {
            Refresh();
        }
    }

    private async Task Invoke(MethodInfo method, object? argument)
    {
        var parameters = method.GetParameters();
        var arguments = parameters.Length switch
        {
            0 => [],
            1 => new[] { Convert(argument, parameters[0].ParameterType) },
            _ => throw new InvalidOperationException($"{typeof(TApp).FullName}.{method.Name} takes more than one parameter; an operation takes at most one input."),
        };
        object? result;
        try { result = method.Invoke(_app, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        switch (result)
        {
            case Task task: await task.ConfigureAwait(false); break;
            case ValueTask valueTask: await valueTask.ConfigureAwait(false); break;
        }
    }

    // The widest public constructor, its parameters from the app's services (capability fakes, the TestOS services).
    private TApp Construct()
    {
        var constructor = typeof(TApp).GetConstructors().OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault()
            ?? throw new InvalidOperationException($"{typeof(TApp).FullName} has no public constructor.");
        var arguments = constructor.GetParameters().Select(parameter => Service(parameter.ParameterType)
            ?? (parameter.HasDefaultValue ? parameter.DefaultValue
                : throw new InvalidOperationException($"App '{Manifest.Id}' needs a {parameter.ParameterType.Name} that the TestOS services do not provide."))).ToArray();
        try { return (TApp)constructor.Invoke(arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static object? Convert(object? argument, Type type)
    {
        if (argument is null)
        {
            if (type == typeof(string)) { return string.Empty; }
            if (type.IsValueType) { return Activator.CreateInstance(type); }
            try { return JsonSerializer.Deserialize("{}", type, Json); }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { return null; }
        }
        return type.IsInstanceOfType(argument)
            ? argument
            : JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(argument, argument.GetType(), Json), type, Json);
    }

    private object? Service(Type type) => _os.Services.GetService(type);

    // Reads the app's presentation after each turn.
    partial void Refresh();

    private static string Key(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
