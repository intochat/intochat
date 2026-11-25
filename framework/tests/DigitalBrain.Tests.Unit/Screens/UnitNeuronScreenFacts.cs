using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using Xunit;

namespace DigitalBrain.Tests.Unit.Screens;

[State("test.screen-unit")]
public sealed record TestScreenUnitState(int Value = 0)
{
    public TestScreenUnitState() : this(0) { }
}

[Signal("test.screen-unit.press")]
public sealed record TestScreenPress(string? By = null) : Signal;

public static class TestScreenUnitSynapses
{
    [Synapse("test.screen-unit")]
    public static View Render(TestScreenUnitState state) =>
        new("Screen Unit", new Column([new Button("btn1", "Press Me", new Command(".", "test.screen-unit.press"))]));

    [Synapse(typeof(TestScreenPress), "test.screen-unit")]
    public static TestScreenUnitState OnPress(TestScreenPress press, TestScreenUnitState state) =>
        state with { Value = state.Value + 1 };
}

public sealed class UnitNeuronScreenFacts
{
    [Fact]
    public void UnitNeuronImplementsIScreenAndIUnit()
    {
        var interfaces = typeof(UnitNeuron<TestScreenUnitState>).GetInterfaces();
        Assert.Contains(typeof(IScreen), interfaces);
        Assert.Contains(typeof(IUnit), interfaces);
        Assert.Contains(typeof(IViewReader), interfaces);
    }

    [Fact]
    public void PlainNeuronContractsRecognizesIScreen()
    {
        Assert.True(PlainNeuronContracts.Is(typeof(IScreen)));
        Assert.Equal("screen", PlainNeuronContracts.TypeOf(typeof(IScreen)));
        Assert.True(PlainNeuronContracts.Is(typeof(IViewReader)));
        Assert.Equal("synapse.view", PlainNeuronContracts.TypeOf(typeof(IViewReader)));
    }

    [Fact]
    public async Task AsksExtensionsInvokeReadStateAndGetJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeUnit = new FakeUnit();
        var json = await fakeUnit.GetJson("readState", JsonSerializer.SerializeToElement(new { }), ct: ct);
        Assert.Equal(100, json.GetProperty("count").GetInt32());

        var text = await fakeUnit.ReadText("readText", JsonSerializer.SerializeToElement(new { }), ct: ct);
        Assert.Equal("sample-text", text);
    }

    private sealed class FakeUnit : IUnit
    {
        public Task<Answer> Handle(Ask ask, CancellationToken ct = default)
        {
            if (ask.Operation == "readState")
            {
                var val = JsonSerializer.SerializeToElement(new { count = 100 });
                return Task.FromResult(new Answer(ask.Slot, ask.Operation, ask.Key, AnswerStatus.Ok, val, null, 0));
            }
            if (ask.Operation == "readText")
            {
                var val = JsonSerializer.SerializeToElement("sample-text");
                return Task.FromResult(new Answer(ask.Slot, ask.Operation, ask.Key, AnswerStatus.Ok, val, null, 0));
            }
            return Task.FromResult(new Answer(ask.Slot, ask.Operation, ask.Key, AnswerStatus.Failed, default, "Unknown", 0));
        }
    }
}
