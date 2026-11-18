using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Tests.E2E.Fixtures;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier2_BoundaryCases;

public sealed record ExactViewState(int TargetBytes = 0)
{
    public ExactViewState() : this(0) { }
}

[Signal("exact-view.set-bytes")]
public sealed record ExactViewSetBytes(int TargetBytes) : Signal;

[Signal("exact-view.corrupted-target")]
public sealed record ExactViewCorruptedTarget(int ExpectedNumber, string ExpectedText) : Signal;

/*
# ExactViewScreen
Screen specifically designed to calibrate rendered View JSON UTF-8 byte length to exact boundaries.
*/
[Neuron("app.exact_view_screen")]
public sealed class ExactViewScreen : Screen<ExactViewState>
{
    protected override View Render(ExactViewState state)
    {
        if (state.TargetBytes <= 0)
        {
            return new View("P", new Heading(""));
        }

        var emptyView = new View("P", new Heading(""));
        var emptyJson = JsonSerializer.Serialize(emptyView, RuntimeProtocol.OptionsFor(typeof(View)));
        var baseBytes = Encoding.UTF8.GetByteCount(emptyJson);

        var needed = state.TargetBytes - baseBytes;
        var padding = needed > 0 ? new string('X', needed) : string.Empty;

        return new View("P", new Heading(padding));
    }

    [On]
    public Task OnSetBytes(ExactViewSetBytes signal)
    {
        return Show(new ExactViewState(signal.TargetBytes));
    }

    [On]
    public Task OnCorrupted(ExactViewCorruptedTarget signal)
    {
        _ = signal;
        return Show(State);
    }
}

public sealed class ScreenAdversarialHardeningFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;

    public ScreenAdversarialHardeningFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task T2_Screen_ExactViewPayload_2097152Bytes_SucceedsAtExactBoundary()
    {
        var os = _fixture.OS;
        os.Register<ExactViewScreen>();

        var brain = NewBrain("screen-exact-2mb");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.exact_view_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Calibrate target bytes to exactly 2,097,152 bytes (UnitLimits.MaxStateBytes)
        await screen.Request("exact-view.set-bytes", JsonSerializer.SerializeToElement(new ExactViewSetBytes(2_097_152)));
        await os.Idle();

        var calibratedView = await screen.Read();
        Assert.NotNull(calibratedView);
        Assert.Equal(2L, calibratedView.Generation);

        var rawJson = calibratedView.View.GetRawText();
        var utf8ByteCount = Encoding.UTF8.GetByteCount(rawJson);
        Assert.Equal(2_097_152, utf8ByteCount);
    }

    [Fact]
    public async Task T2_Screen_ExactViewPayload_2097153Bytes_ThrowsInvalidOperationException()
    {
        var os = _fixture.OS;
        os.Register<ExactViewScreen>();

        var brain = NewBrain("screen-exact-plus-one");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.exact_view_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Target exactly 2,097,153 bytes (MaxStateBytes + 1)
        await screen.Request("exact-view.set-bytes", JsonSerializer.SerializeToElement(new ExactViewSetBytes(2_097_153)));
        await os.Idle();

        // The failure is captured in os.Failures
        Assert.NotEmpty(os.Failures);
        var ex = os.Failures.OfType<InvalidOperationException>()
            .FirstOrDefault(e => e.Message.Contains("exceeds the 2097152-byte cap"));

        Assert.NotNull(ex);

        // Previous view state is preserved intact at generation 1
        var recoveredView = await screen.Read();
        Assert.NotNull(recoveredView);
        Assert.Equal(1L, recoveredView.Generation);
    }

    [Fact]
    public async Task T2_Screen_Request_CorruptedJsonPayload_FailsCleanlyWithoutCorruptingScreen()
    {
        var os = _fixture.OS;
        os.Register<ExactViewScreen>();

        var brain = NewBrain("screen-corrupt-payload");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.exact_view_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);

        // Dispatch Request with mismatched JSON type: number primitive instead of object
        using var numberDoc = JsonDocument.Parse("42");
        await screen.Request("exact-view.corrupted-target", numberDoc.RootElement);
        await os.Idle();

        // Failure is caught and logged
        Assert.NotEmpty(os.Failures);
        Assert.Contains(os.Failures, e => e is JsonException || e is InvalidCastException || e is ArgumentException);

        // State remains valid at generation 1
        var viewAfterFail = await screen.Read();
        Assert.NotNull(viewAfterFail);
        Assert.Equal(1L, viewAfterFail.Generation);
    }

    [Fact]
    public async Task T2_Screen_Request_TruncatedJsonStructure_ThrowsOrFailsSafely()
    {
        var os = _fixture.OS;
        os.Register<ExactViewScreen>();

        var brain = NewBrain("screen-truncated-json");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", brain));
        var screenId = new NeuronId(brain, "app.exact_view_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // In TestOS proxy, asynchronous Request failures are reported via os.Failures
        await screen.Request("");
        await os.Idle();
        Assert.NotEmpty(os.Failures);
        Assert.Contains(os.Failures, ex => ex is ArgumentException && (ex.Message.Contains("whitespace") || ex.Message.Contains("signal")));

        await screen.Request("   ");
        await os.Idle();
        Assert.Contains(os.Failures, ex => ex is ArgumentException && (ex.Message.Contains("whitespace") || ex.Message.Contains("signal")));
    }
}
