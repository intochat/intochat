using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using IntoChat.OS.Modules.Flutter.FileInput;
using IntoChat.OS.Modules.Flutter.Select;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.TextField.Signals;
using IntoChat.OS.Modules.Flutter.VoiceInput;
using IntoChat.OS.Modules.Flutter.VoiceInput.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration;

public sealed class PrimitiveBindingFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task UserEventsReachDurableHandlerWhileProjectionDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var handler = brain.Get<IPrimitiveTestHandler>("owner/handler");
        var field = brain.Get<ITextField>("owner/field");
        var binding = brain.Get<IUiBinding>("owner/field");
        await binding.Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, ct);
        await Assert.ThrowsAsync<ArgumentException>(() => field.Configure("Message", "multiline", new string('x', 513)));
        await field.Configure("Message", "multiline", "workspace/app/send");
        await field.SetValue("projection");
        Assert.Empty((await handler.Events()).Decoded());
        await field.Input("first edit");
        await WaitFor(handled, 1, ct);
        Assert.Equal(new TextFieldChanged("owner/field", "first edit"), Assert.Single((await handler.Events()).Decoded()));
        await brain.DeactivateAsync(binding, ct);
        await brain.DeactivateAsync(field, ct);
        await field.Input("second edit");
        await WaitFor(handled, 2, ct);
        Assert.Equal(new TextFieldChanged("owner/field", "second edit"), (await handler.Events()).Decoded()[1]);
        Assert.Equal("second edit", (await field.Read()).Value);
        Assert.Equal("workspace/app/send", (await field.Read()).SubmitButton);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SelectFileButtonAndVoiceDispatchTypedSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var handler = brain.Get<IPrimitiveTestHandler>("owner/handler");
        foreach (var key in new[] { "owner/select", "owner/file", "owner/button", "owner/voice" }) { await brain.Get<IUiBinding>(key).Bind(UiHandlers.IdOf(handler)); }
        await using var handled = await brain.Observe<UiEventHandled>(handler, ct);
        var select = brain.Get<ISelect>("owner/select");
        await select.Set("Model", [new("", "Automatic"), new("yes", "Ready"), new("no", "Unavailable", false)], "");
        await Assert.ThrowsAsync<ArgumentException>(() => select.Choose("no"));
        await Assert.ThrowsAsync<ArgumentException>(() => select.Choose("missing"));
        await select.Choose("yes");
        var file = brain.Get<IFileInput>("owner/file");
        await file.Configure("Attach");
        await Assert.ThrowsAsync<ArgumentException>(() => file.Capture("large.txt", new string('x', 32_001)));
        await file.Capture("notes.txt", "notes");
        var button = brain.Get<IButton>("owner/button");
        await button.Set("Send", "submit");
        await button.Click();
        var voice = brain.Get<IVoiceInput>("owner/voice");
        await voice.Capture([1, 2], "audio/wav");
        await WaitFor(handled, 4, ct);
        var events = (await handler.Events()).Decoded();
        Assert.Contains(events, signal => signal is SelectChanged { Name: "owner/select", Value: "yes" });
        Assert.Contains(events, signal => signal is FileCaptured { Name: "owner/file", FileName: "notes.txt", Content: "notes" });
        Assert.Contains(events, signal => signal is ButtonClicked { Name: "owner/button", Action: "submit" });
        Assert.Contains(events, signal => signal is VoiceCaptured { Name: "owner/voice", MimeType: "audio/wav" });
        await brain.DeactivateAsync(select, ct);
        Assert.Equal("yes", (await select.Read()).Selected);
        Assert.Equal(3, (await select.Read()).Options.Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UnboundInputsWorkAndNavigationPayloadsAreValidated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        await brain.Get<ITextField>("owner/field").Input("edit");
        await brain.Get<IFileInput>("owner/file").Capture("empty.txt", "");
        var select = brain.Get<ISelect>("owner/select");
        await select.Set("Choice", [new("a", "A")], "a");
        await select.Choose("a");
        var button = brain.Get<IButton>("owner/open");
        await Assert.ThrowsAsync<ArgumentException>(() => button.SetActivation("Open", "[]"));
        await Assert.ThrowsAsync<ArgumentException>(() => button.SetActivation("Open", "invalid"));
        await button.SetActivation("Open", "{\"windowId\":\"table-1\"}");
        Assert.Equal("activate", (await button.Read()).Action);
        Assert.NotNull((await button.Read()).Activation);
        await brain.DeactivateAsync(button, ct);
        Assert.NotNull((await button.Read()).Activation);
        await button.Set("Send", "submit");
        Assert.Null((await button.Read()).Activation);
        await button.Click();
    }

    // D-05 / T-06-10 (06-07b): a binding hands events to a neuron of its own brain only, named by its runtime id.
    [Trait("host", "own")]
    [Fact]
    public async Task ABindingRefusesAHandlerOfAnotherBrainOrWithoutAType()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var binding = brain.Get<IUiBinding>("owner/guarded");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => binding.Bind("someone-else/test.primitive-handler/handler"));
        await Assert.ThrowsAsync<ArgumentException>(() => binding.Bind("owner/handler"));
        await binding.Bind(UiHandlers.IdOf(brain.Get<IPrimitiveTestHandler>("owner/handler")));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task HandlerCanProjectAnEditWithoutRedispatchingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var handler = brain.Get<IPrimitiveTestHandler>("owner/projecting-handler");
        var field = brain.Get<ITextField>("owner/projection-field");
        await handler.ProjectTo("owner/projection-field");
        await brain.Get<IUiBinding>("owner/projection-field").Bind(UiHandlers.IdOf(handler));
        await using var handled = await brain.Observe<UiEventHandled>(handler, ct);
        await field.Input("edited");
        await WaitFor(handled, 1, ct);
        Assert.Equal("projected: edited", (await field.Read()).Value);
        Assert.Equal(new TextFieldChanged("owner/projection-field", "edited"), Assert.Single((await handler.Events()).Decoded()));
        await field.SetValue("another projection");
        Assert.Single((await handler.Events()).Decoded());
    }
    // The handler announces each event it has recorded; delivery is at-least-once, so wait for the count.
    internal static async Task WaitFor(SignalProbe<UiEventHandled> handled, int count, CancellationToken ct)
    {
        while ((await handled.NextAsync(ct: ct)).Count < count) { }
    }
}

[GenerateSerializer, Alias("test.ui-event-handled")]
public sealed record UiEventHandled([property: Id(0)] int Count, [property: Id(1)] string Type) : Signal;

[Alias("test.primitive-handler"), Orleans.Metadata.DefaultGrainType("test.primitive-handler")]
public interface IPrimitiveTestHandler : IUiEventHandler, INeuron
{
    // The events as they arrived; decode them with UiEvents.Decoded (a plain control's [Signal] record has no Orleans form).
    Task<IReadOnlyList<UiEvent>> Events();
    Task ProjectTo(string name);
}

[GrainType("test.primitive-handler")]
public sealed class PrimitiveTestHandler : Neuron, IPrimitiveTestHandler
{
    private readonly List<UiEvent> _events = [];
    private string? _projection;
    public Task ProjectTo(string name) { _projection = name; return Task.CompletedTask; }

    public async Task HandleUiEvent(UiEvent uiEvent)
    {
        _events.Add(uiEvent);
        if (_projection is not null && uiEvent.As<TextFieldChanged>() is { } changed)
        { await ServiceProvider.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().Get<ITextField>(_projection).SetValue("projected: " + changed.Value); }
        await PublishAsync(new UiEventHandled(_events.Count, uiEvent.Type[(uiEvent.Type.LastIndexOf('.') + 1)..]));
    }
    public Task<IReadOnlyList<UiEvent>> Events() => Task.FromResult<IReadOnlyList<UiEvent>>(_events.ToArray());
}

public static class UiEvents
{
    // The signal types a control dispatches, by the full name a UiEvent carries: os Signals and plain [Signal] records.
    private static readonly Dictionary<string, Type> Dispatched = typeof(IUiBinding).Assembly.GetTypes()
        .Where(type => typeof(Signal).IsAssignableFrom(type) || typeof(DigitalBrain.Signal).IsAssignableFrom(type))
        .ToDictionary(type => type.FullName!, StringComparer.Ordinal);

    public static object[] Decoded(this IReadOnlyList<UiEvent> events)
        => [.. events.Select(uiEvent => System.Text.Json.JsonSerializer.Deserialize(uiEvent.Json, Dispatched[uiEvent.Type], System.Text.Json.JsonSerializerOptions.Web)!)];
}
