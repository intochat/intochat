using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Sandbox;

// The generic entry of a program that is a framework [App] class instead of a script: it builds the app from the framework's own
// AppDefinition (the rules TestOS applies) and maps each brain.On(app, operation) request onto the matching [On] method, one request at a
// time, as turns. What the app presents goes to the app's view neurons through the client. A program is an [App] class when its assembly has
// no entry point and declares exactly one.
internal static class AppRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Type? AppTypeIn(Assembly assembly)
    {
        var apps = assembly.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false } && type.IsDefined(typeof(AppAttribute), false)).ToArray();
        return apps.Length == 1 ? apps[0] : null;
    }

    public static async Task<int> RunAsync(Type appType, CancellationToken stopping)
    {
        await using var brain = await IntoChatOSClient.ConnectAsync([], stopping).ConfigureAwait(false);
        var appKey = brain.Setting("App") ?? throw new InvalidOperationException("An [App] program is assigned with the Setting \"App\": its app key.");
        var definition = new AppDefinition(appType);
        var presenter = new ViewPresenter(brain, appKey, long.Parse(brain.Setting("ProgramRevision") ?? "1", System.Globalization.CultureInfo.InvariantCulture));
        var instance = definition.Create(service => service == typeof(IPresenter) ? presenter : null);
        var app = brain.Get<IApp>(appKey);
        // The operations the app is requested by are the manifest's; each one that is an [On] method is served.
        var snapshot = await app.Read().ConfigureAwait(false);
        var turns = new SemaphoreSlim(1, 1);
        var loops = snapshot.Manifest.Operations.Where(operation => definition.FindOperation(operation.Name) is not null)
            .Select(operation => Serve(brain, app, definition, instance, operation.Name, turns, stopping)).ToArray();
        try { await Task.WhenAll(loops).ConfigureAwait(false); }
        finally { await presenter.DrainAsync().ConfigureAwait(false); }
        return 0;
    }

    private static async Task Serve(IntoChatOSConnection brain, IApp app, AppDefinition definition, App instance, string operation, SemaphoreSlim turns, CancellationToken stopping)
    {
        try
        {
            await foreach (var request in brain.On(app, operation, stopping).ConfigureAwait(false))
            {
                await turns.WaitAsync(stopping).ConfigureAwait(false);
                try { await Turn(definition, instance, operation, request, stopping).ConfigureAwait(false); }
                finally { turns.Release(); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    // An operation that throws has failed its request, not the app: the failure goes to the run log, and the next request is served.
    private static async Task Turn(AppDefinition definition, App instance, string operation, AppRequested request, CancellationToken stopping)
    {
        try { await definition.Invoke(instance, operation, Argument(definition.FindOperation(operation)!, request.Input), Json, stopping).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        { Console.Error.WriteLine($"{definition.Manifest.Id}.{operation} failed for request {request.RequestId}: {error}"); }
    }

    // A request's input is text. An operation that takes a string gets it as is; one that takes a signal or a value gets it read as JSON,
    // an empty input being the empty value, as TestOS requests an operation without an argument.
    private static object? Argument(MethodInfo method, string input)
    {
        var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType != typeof(CancellationToken));
        if (parameter is null || parameter.ParameterType == typeof(string)) { return string.IsNullOrEmpty(input) ? null : input; }
        return string.IsNullOrWhiteSpace(input) ? null : JsonSerializer.Deserialize(input, parameter.ParameterType, Json);
    }
}
