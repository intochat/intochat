using System.Reflection;
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

public sealed class UnitLimitTestState
{
    public int Count { get; init; }
    public string Data { get; init; } = string.Empty;
}

[Signal("unit-limit.expand")]
public sealed record UnitLimitExpand(int Size) : Signal;

/*
# UnitLimitTest
Screen for testing 2MB state byte boundaries.
*/
[Neuron("app.unit_limit_screen")]
public sealed class UnitLimitTestScreen : Screen<UnitLimitTestState>
{
    protected override View Render(UnitLimitTestState state)
    {
        return new View("UnitLimit", new Column([
            new Text($"Count: {state.Count}"),
            new Text(state.Data),
            new Button("btn-expand", "Expand", Emits(new UnitLimitExpand(2_097_153)))
        ]));
    }

    [On]
    public Task OnExpand(UnitLimitExpand signal)
    {
        var next = new UnitLimitTestState
        {
            Count = State.Count + 1,
            Data = OversizedPayloadBuilder.CreateExactString(signal.Size)
        };
        return Show(next);
    }
}

public sealed class UnitMachineStateLimitFacts : E2ETestBase, IClassFixture<TestOSFixture>
{
    private readonly TestOSFixture _fixture;
    private static readonly MethodInfo RequireFitsGeneric = ResolveRequireFitsMethod();

    public UnitMachineStateLimitFacts(TestOSFixture fixture)
    {
        _fixture = fixture;
    }

    private static MethodInfo ResolveRequireFitsMethod()
    {
        var assembly = typeof(Screen<>).Assembly;
        var limitsType = assembly.GetType("DigitalBrain.Units.UnitLimits", throwOnError: true)!;
        var method = limitsType.GetMethod("RequireFits", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return method;
    }

    private static void InvokeRequireFits<TState>(TState state, NeuronId id)
    {
        var typedMethod = RequireFitsGeneric.MakeGenericMethod(typeof(TState));
        try
        {
            typedMethod.Invoke(null, [state, id]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void T2_UnitLimits_RequireFits_Exact2MbBoundary_Succeeds()
    {
        // 1. Construct state whose JSON serialization UTF-8 byte count is exactly 2,097,152 bytes
        var exactState = OversizedPayloadBuilder.CreateStateOfSerializedLength(2_097_152);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(exactState, RuntimeProtocol.OptionsFor(typeof(OversizedPayloadBuilder.ExactByteState)));
        Assert.Equal(2_097_152, serializedBytes.Length);

        var neuronId = new NeuronId(new BrainId("unit-limit-exact"), "unit.test", "default");

        // 2. Exact 2 MB cap passes without exception
        InvokeRequireFits(exactState, neuronId);
    }

    [Fact]
    public void T2_UnitLimits_RequireFits_2MbPlusOneByte_ThrowsInvalidOperationException()
    {
        // 1. Construct state whose serialized length is exactly 2,097,153 bytes (MaxStateBytes + 1)
        var oversizedState = OversizedPayloadBuilder.CreateStateOfSerializedLength(2_097_153);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(oversizedState, RuntimeProtocol.OptionsFor(typeof(OversizedPayloadBuilder.ExactByteState)));
        Assert.Equal(2_097_153, serializedBytes.Length);

        var neuronId = new NeuronId(new BrainId("unit-limit-plus-one"), "unit.test", "default");

        // 2. MaxStateBytes + 1 byte throws InvalidOperationException mentioning cap
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeRequireFits(oversizedState, neuronId));

        Assert.Contains("exceeding the 2097152-byte cap", ex.Message);
    }

    [Fact]
    public void T2_UnitLimits_RequireFits_FarExceeding2Mb_ThrowsInvalidOperationException()
    {
        // 1. Construct state of 5 MB
        const int fiveMb = 5 * 1024 * 1024;
        var extremeState = OversizedPayloadBuilder.CreateStateOfSerializedLength(fiveMb);
        var neuronId = new NeuronId(new BrainId("unit-limit-5mb"), "unit.test", "default");

        // 2. Far exceeding cap throws InvalidOperationException
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InvokeRequireFits(extremeState, neuronId));

        Assert.Contains("exceeding the 2097152-byte cap", ex.Message);
    }

    [Fact]
    public async Task T2_UnitNeuron_StateExceeding2Mb_RollsBackTransactionAndLeavesStateUnchanged()
    {
        var os = _fixture.OS;
        os.Register<UnitLimitTestScreen>();

        var brain = NewBrain("unit-rollback");
        var screenId = new NeuronId(brain, "app.unit_limit_screen", "default");
        var screen = os.Get<IScreen>(screenId);

        // Initial valid state at generation 1
        var initialView = await screen.Read();
        Assert.NotNull(initialView);
        Assert.Equal(1L, initialView.Generation);
        Assert.Contains("Count: 0", initialView.View.GetRawText());

        // Activate button that triggers expansion beyond 2 MB
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            screen.Activate("app.unit_limit_screen", 1L, "btn-expand"));

        Assert.Contains("exceeds the 2097152-byte cap", ex.Message);

        // State remains at generation 1 and Count = 0 (transaction rollback, zero state mutation)
        var preservedView = await screen.Read();
        Assert.NotNull(preservedView);
        Assert.Equal(1L, preservedView.Generation);
        Assert.Contains("Count: 0", preservedView.View.GetRawText());
    }
}
