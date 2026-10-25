using System.Text.Json;
using IntoChat.OS;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Flutter;

// A UI event as the bound handler receives it (06-07b, R4-17): the control's signal as its CLR type name and JSON, and the
// caller the dispatch ran for. One payload for grain and plain controls alike: it crosses the runtime's JSON envelope and,
// until the legacy handlers migrate (06-06a), an Orleans grain call, hence the serializer ids. The caller is a cross-check
// only: a handler authorizes on the stamped caller of the call it runs in, never on this field alone.
[GenerateSerializer, Alias("ui.event")]
public sealed record UiEvent(
    [property: Id(0)] string Type,
    [property: Id(1)] string Json,
    [property: Id(2)] UiEventCaller? Caller)
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    // The event of a control signal (an os Signal or a DigitalBrain [Signal] record), without a caller; the binding adds it.
    public static UiEvent Of(object signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return new(signal.GetType().FullName!, JsonSerializer.Serialize(signal, signal.GetType(), Options), null);
    }

    public bool Is<T>() => Type == typeof(T).FullName;

    // The signal when it is a T, else null.
    public T? As<T>() where T : class => Is<T>() ? JsonSerializer.Deserialize<T>(Json, Options) : null;
}

// The stamped caller of a dispatch: who acted on the control.
[GenerateSerializer, Alias("ui.event-caller")]
public sealed record UiEventCaller(
    [property: Id(0)] string PrincipalId,
    [property: Id(1)] string AccountId,
    [property: Id(2)] string BrainId,
    [property: Id(3)] string Kind,
    [property: Id(4)] string StampedBy,
    [property: Id(5)] string? AppId,
    [property: Id(6)] string? OnBehalfOf);

[Alias("ui.event-handler")]
public interface IUiEventHandler : IGrainWithStringKey
{
    [OneWay] Task HandleUiEvent(UiEvent uiEvent);
}

[Alias("ui.binding"), Orleans.Metadata.DefaultGrainType("ui.binding")]
public interface IUiBinding : INeuron
{
    // The handler is a neuron of the binding's own brain, named by its runtime id text "brain/type/key" (D-05; text, so the
    // id crosses an Orleans call from a legacy binder as well as the JSON envelope); another brain's handler is refused.
    Task Bind(string handler);
    Task Dispatch(UiEvent uiEvent);
}

// Transitional (06-07b; until the legacy handlers migrate in 06-06a): the runtime id a grain handler binds itself with.
public static class UiHandlers
{
    public static string IdOf(IAddressable grain)
    {
        ArgumentNullException.ThrowIfNull(grain);
        var id = grain.GetGrainId();
        var key = id.Key.ToString()!;
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) { throw new ArgumentException("A UI handler grain is keyed {brain}/{key}.", nameof(grain)); }
        return $"{key[..slash]}/{id.Type}/{key[(slash + 1)..]}";
    }
}
