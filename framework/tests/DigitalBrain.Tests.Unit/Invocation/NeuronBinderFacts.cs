// CA1068: Tokened deliberately takes tokens first and last (token injection anywhere). xUnit1051: the binder facts pass their own tokens on purpose. CA1861: inline sample arguments.
#pragma warning disable CA1068, xUnit1051, CA1861
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Invocation;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class NeuronBinderFacts
{
    public sealed record Item(string Name, int Count);
    public enum Tone { Info, Warning }
    [JsonConverter(typeof(JsonStringEnumConverter<StringTone>))]
    public enum StringTone { Info, Warning }
    public sealed record Action(string Kind, string Argument);

    public interface IProbe : IDisposable
    {
        Task<int> Add(int value);
        Task<string> Add(string value);
        Task<int> Twice(int value, CancellationToken cancellationToken);
        Task<string> Tokened(CancellationToken first, string text, CancellationToken second);
        void Touch();
        Task Reset();
        ValueTask Clear();
        ValueTask<Item> Find(string name);
        IAsyncEnumerable<int> Count(int upTo, CancellationToken cancellationToken);
        IAsyncEnumerable<int> Forever(CancellationToken cancellationToken);
        Task<long> Wide(long value);
        Task<double> Wide(double value);
        Task Fail();
        Task Put(Item item);
        Task<string> Point(string target, Tone tone, string reason, Action? action = null);
        Task<string> Make(string goal);
        Task<string> Make(string? alias, string? title, string goal);
        Task<int> List(int limit = 10);
        Task<string> DefaultedTone(StringTone? tone = StringTone.Warning);
        Task<string> Run(string tool, string arguments);
        Task<string> Run(string tool, JsonElement args);
    }

    public sealed class Probe : IProbe
    {
        public int Touched { get; private set; }
        public CancellationToken? First { get; private set; }
        public CancellationToken? Second { get; private set; }

        public Task<int> Add(int value) => Task.FromResult(value + 1);
        public Task<string> Add(string value) => Task.FromResult(value + "!");
        public Task<int> Twice(int value, CancellationToken cancellationToken) { First = cancellationToken; return Task.FromResult(value * 2); }
        public Task<string> Tokened(CancellationToken first, string text, CancellationToken second) { First = first; Second = second; return Task.FromResult(text); }
        public void Touch() => Touched++;
        public Task Reset() => Task.CompletedTask;
        public ValueTask Clear() => ValueTask.CompletedTask;
        public async ValueTask<Item> Find(string name) { await Task.Yield(); return new Item(name, 3); }
        public async IAsyncEnumerable<int> Count(int upTo, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 1; i <= upTo; i++) { await Task.Yield(); yield return i; }
        }
        public async IAsyncEnumerable<int> Forever([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; ; i++) { await Task.Delay(1, cancellationToken); yield return i; }
        }
        public Task<long> Wide(long value) => Task.FromResult(value);
        public Task<double> Wide(double value) => Task.FromResult(value);
        public Task Fail() => throw new InvalidTimeZoneException("boom");
        public Task Put(Item item) => Task.CompletedTask;
        public Task<string> Point(string target, Tone tone, string reason, Action? action = null) => Task.FromResult($"{target}:{tone}:{reason}:{action?.Kind}");
        public Task<string> Make(string goal) => Task.FromResult(goal);
        public Task<string> Make(string? alias, string? title, string goal) => Task.FromResult($"{alias}:{title}:{goal}");
        public Task<int> List(int limit = 10) => Task.FromResult(limit);
        public Task<string> DefaultedTone(StringTone? tone = StringTone.Warning) => Task.FromResult(tone?.ToString() ?? "none");
        public Task<string> Run(string tool, string arguments) => Task.FromResult($"string:{tool}:{arguments}");
        public Task<string> Run(string tool, JsonElement args) => Task.FromResult($"json:{tool}:{args.GetRawText()}");
        public void Dispose() { }
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, RuntimeProtocol.Json);

    private static RuntimeInvocation Call(string method, params JsonElement[] arguments)
        => new("behavior", typeof(IProbe).FullName!, "b1/probe/a", method, arguments);

    private static async Task<JsonElement?> Run(NeuronBinder binder, object target, RuntimeInvocation call, CancellationToken token = default)
        => await binder.Bind(typeof(IProbe), call).InvokeAsync(target, call.Arguments, token);

    [Fact]
    public async Task Overloads_bind_by_parameter_type_and_ambiguity_names_contract_and_method()
    {
        var binder = new NeuronBinder();
        var probe = new Probe();

        Assert.Equal(3, (await Run(binder, probe, Call("Add", Json(2))))!.Value.GetInt32());
        Assert.Equal("a!", (await Run(binder, probe, Call("Add", Json("a"))))!.Value.GetString());

        var error = Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("Wide", Json(2))));
        Assert.Contains("IProbe", error.Message, StringComparison.Ordinal);
        Assert.Contains("Wide", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_method_arity_malformed_and_oversized_arguments_raise_binding_errors()
    {
        var binder = new NeuronBinder(maximumArgumentBytes: 64);
        var probe = new Probe();

        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("Nope")));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("Reset", Json(1))));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("Add", Json(new[] { 1, 2 }))));
        await Assert.ThrowsAsync<BindingException>(() => Run(binder, probe, Call("Find", Json(5))));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("Find", Json(new string('x', 100)))));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), null!));
    }

    [Fact]
    public void Object_and_disposal_members_are_not_bindable()
    {
        var binder = new NeuronBinder();

        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("Dispose")));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(IProbe), Call("GetHashCode")));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(Probe), Call("ToString")));
        Assert.Throws<BindingException>(() => binder.Bind(typeof(Probe), Call("Dispose")));
    }

    [Fact]
    public async Task Cancellation_tokens_receive_the_caller_token_wherever_they_appear()
    {
        var binder = new NeuronBinder();
        var probe = new Probe();
        using var source = new CancellationTokenSource();

        Assert.Equal(8, (await Run(binder, probe, Call("Twice", Json(4)), source.Token))!.Value.GetInt32());
        Assert.Equal(source.Token, probe.First);
        Assert.Equal("t", (await Run(binder, probe, Call("Tokened", Json("t")), source.Token))!.Value.GetString());
        Assert.Equal(source.Token, probe.First);
        Assert.Equal(source.Token, probe.Second);
    }

    [Fact]
    public async Task Every_result_shape_returns_json_and_streams_stop_on_cancel()
    {
        var binder = new NeuronBinder();
        var probe = new Probe();

        Assert.Null(await Run(binder, probe, Call("Touch")));
        Assert.Equal(1, probe.Touched);
        Assert.Null(await Run(binder, probe, Call("Reset")));
        Assert.Null(await Run(binder, probe, Call("Clear")));
        Assert.Equal(new Item("n", 3), (await Run(binder, probe, Call("Find", Json("n"))))!.Value.Deserialize<Item>(RuntimeProtocol.Json));

        var count = Call("Count", Json(3));
        List<int> items = [];
        await foreach (var item in binder.Bind(typeof(IProbe), count).StreamAsync(probe, count.Arguments, CancellationToken.None))
        {
            items.Add(item.GetInt32());
        }
        Assert.Equal([1, 2, 3], items);

        var forever = Call("Forever");
        using var source = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in binder.Bind(typeof(IProbe), forever).StreamAsync(probe, forever.Arguments, source.Token))
            {
                if (++seen == 2) { await source.CancelAsync(); }
            }
        });
        Assert.Equal(2, seen);
    }

    [Fact]
    public async Task Target_exceptions_surface_as_their_original_type()
    {
        await Assert.ThrowsAsync<InvalidTimeZoneException>(() => Run(new NeuronBinder(), new Probe(), Call("Fail")));
    }

    [Fact]
    public void Second_bind_returns_the_catalog_descriptor()
    {
        var binder = new NeuronBinder();

        var first = binder.Bind(typeof(IProbe), Call("Twice", Json(1)));
        var second = binder.Bind(typeof(IProbe), Call("Twice", Json(5)));

        Assert.Same(first, second);
    }

    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void NamedArgumentsBindInParameterOrder()
    {
        var call = NeuronBinder.Arrange(typeof(IProbe), "Point", J("""{"reason":"why","target":"window:settings","tone":"Info"}"""));
        Assert.Equal(["\"window:settings\"", "\"Info\"", "\"why\"", "null"], call.Arguments.Select(a => a.GetRawText()));
    }

    [Fact]
    public void SnakeCaseAndPascalCaseNamesMatch()
    {
        Assert.Equal("\"g\"", NeuronBinder.Arrange(typeof(IProbe), "Make", J("""{"Goal":"g"}""")).Arguments[0].GetRawText());
        Assert.Equal("3", NeuronBinder.Arrange(typeof(IProbe), "Count", J("""{"up_to":3}""")).Arguments[0].GetRawText());
    }

    [Fact]
    public void MissingNamedArgumentIsNamed()
    {
        var error = Assert.Throws<BindingException>(() => NeuronBinder.Arrange(typeof(IProbe), "Point", J("""{"target":"t","tone":"Info"}""")));
        Assert.Contains("missing 'reason'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Point(target, tone, reason[, action])", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownNamedArgumentIsRefusedWithTheKnownNames()
    {
        var error = Assert.Throws<BindingException>(() => NeuronBinder.Arrange(typeof(IProbe), "Make", J("""{"goal":"g","colour":"red"}""")));
        Assert.Contains("'colour'", error.Message, StringComparison.Ordinal);
        Assert.Contains("goal", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayArgumentsStillBind()
    {
        var call = NeuronBinder.Arrange(typeof(IProbe), "Point", J("""["t","Info","r"]"""));
        Assert.Equal(4, call.Arguments.Length);
        Assert.Equal(["\"t\"", "\"Info\"", "\"r\"", "null"], call.Arguments.Select(a => a.GetRawText()));
    }

    [Fact]
    public void OverloadIsChosenByNames()
    {
        Assert.Single(NeuronBinder.Arrange(typeof(IProbe), "Make", J("""{"goal":"g"}""")).Method.GetParameters());
        var call = NeuronBinder.Arrange(typeof(IProbe), "Make", J("""{"alias":"a","goal":"g"}"""));
        Assert.Equal(3, call.Method.GetParameters().Length);
        Assert.Equal(["\"a\"", "null", "\"g\""], call.Arguments.Select(a => a.GetRawText()));
    }

    [Fact]
    public void AStringIsNotArguments() =>
        Assert.Throws<BindingException>(() => NeuronBinder.Arrange(typeof(IProbe), "Make", J("\"g\"")));

    [Fact]
    public void UndefinedAndNullBindZeroArgAndDefaultedMethods()
    {
        Assert.Empty(NeuronBinder.Arrange(typeof(IProbe), "Reset", default).Arguments);
        Assert.Empty(NeuronBinder.Arrange(typeof(IProbe), "Reset", J("null")).Arguments);
        Assert.Equal(["10"], NeuronBinder.Arrange(typeof(IProbe), "List", default).Arguments.Select(a => a.GetRawText()));
        Assert.Equal(["10"], NeuronBinder.Arrange(typeof(IProbe), "List", J("{}")).Arguments.Select(a => a.GetRawText()));
        Assert.Equal(["10"], NeuronBinder.Arrange(typeof(IProbe), "List", J("""{"limit":null}""")).Arguments.Select(a => a.GetRawText()));
        Assert.Equal(["10"], NeuronBinder.Arrange(typeof(IProbe), "List", J("[null]")).Arguments.Select(a => a.GetRawText()));
    }

    [Fact]
    public void NamedOverloadsWithSameParameterDisambiguateByTypeAndRejectAmbiguity()
    {
        Assert.Equal(typeof(int), NeuronBinder.Arrange(typeof(IProbe), "Add", J("""{"value":2}""")).Method.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(string), NeuronBinder.Arrange(typeof(IProbe), "Add", J("""{"value":"a"}""")).Method.GetParameters()[0].ParameterType);
        Assert.Throws<BindingException>(() => NeuronBinder.Arrange(typeof(IProbe), "Wide", J("""{"value":2}""")));
    }

    [Fact]
    public void DuplicateNormalizedNamedArgumentsAreRefused()
    {
        Assert.Throws<BindingException>(() => NeuronBinder.Arrange(typeof(IProbe), "Make", J("""{"goal":"a","Goal":"b"}""")));
    }

    [Fact]
    public void NullableEnumDefaultSerializesAsEnum()
    {
        var named = NeuronBinder.Arrange(typeof(IProbe), "DefaultedTone", J("{}"));
        Assert.Equal(["\"Warning\""], named.Arguments.Select(a => a.GetRawText()));

        var positional = NeuronBinder.Arrange(typeof(IProbe), "DefaultedTone", J("[]"));
        Assert.Equal(["\"Warning\""], positional.Arguments.Select(a => a.GetRawText()));
    }

    [Fact]
    public async Task OverloadPrefersConcreteTypeOverCatchAllJsonElement()
    {
        var binder = new NeuronBinder();
        var probe = new Probe();

        var stringResult = (await Run(binder, probe, Call("Run", Json("echo"), Json("{}"))))!.Value.GetString();
        Assert.Equal("string:echo:{}", stringResult);

        var jsonResult = (await Run(binder, probe, Call("Run", Json("echo"), J("""{"x":1}"""))))!.Value.GetString();
        Assert.Equal("""json:echo:{"x":1}""", jsonResult);

        var arrangedString = NeuronBinder.Arrange(typeof(IProbe), "Run", J("""["echo","{}"]"""));
        Assert.Equal(typeof(string), arrangedString.Method.GetParameters()[1].ParameterType);

        var arrangedJson = NeuronBinder.Arrange(typeof(IProbe), "Run", J("""["echo",{"x":1}]"""));
        Assert.Equal(typeof(JsonElement), arrangedJson.Method.GetParameters()[1].ParameterType);
    }
}
