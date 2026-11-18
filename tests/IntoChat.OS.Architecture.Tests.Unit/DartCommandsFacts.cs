using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;

namespace IntoChat.OS.Architecture.Tests.Unit;

// One typed Dart sender per control neuron command: act<Kind><Command>(ctx, address, <args>) posts the command to that neuron.
public sealed class DartCommandsFacts
{
    private static readonly string Target = Path.Combine(Repository.Root, "ui", "lib", "s" + "rc", "generated", "node_commands.dart");

    [Fact]
    public void TheDartCommandSendersAreGenerated()
        => NodeSchema.CheckOrRegenerate(Target, NodeSchema.GenerateCommands(), nameof(TheDartCommandSendersAreGenerated));

    [Fact]
    public void EveryCommandOfAControlNeuronHasASender()
    {
        var generated = NodeSchema.GenerateCommands();
        foreach (var neuron in NodeSchema.ControlNeurons())
        {
            foreach (var command in NodeSchema.Commands(neuron))
            {
                Assert.Contains($"{NodeSchema.SenderName(neuron, command)}(NodeContext ctx, String address", generated, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_sample_control_neuron_gets_typed_senders_and_a_catalog_entry()
    {
        var dart = NodeSchema.GenerateCommands([typeof(SampleCheckBox)]);
        Assert.Contains("Future<ActivationOutcome> actSampleCheckBoxToggle(NodeContext ctx, String address) =>\n    ctx.act(address, 'control.toggle', {});", dart, StringComparison.Ordinal);
        Assert.Contains("actSampleCheckBoxSetChecked(NodeContext ctx, String address, {required bool isOn}) =>\n    ctx.act(address, 'control.setChecked', {'isOn': isOn});", dart, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated", dart, StringComparison.Ordinal);

        var catalog = NodeSchema.GenerateCatalog([typeof(SampleCheckBox)]);
        Assert.Contains("\"kind\": \"sampleCheckBox\",\n    \"isNeuron\": true,", catalog, StringComparison.Ordinal);
        Assert.Contains("\"signal\": \"control.toggled\"", catalog, StringComparison.Ordinal);
    }

    internal sealed class SampleCheckBoxState
    {
        public bool IsOn { get; init; }
    }

    internal sealed record Unrelated : Signal;

    [Emits(typeof(Toggled))]
    internal sealed class SampleCheckBox : Screen<SampleCheckBoxState>
    {
        [On]
        public Task Toggle(Toggle _) => Show(new SampleCheckBoxState { IsOn = !State.IsOn }, new Toggled(!State.IsOn));

        [On]
        public Task SetChecked(SetChecked command) => Show(new SampleCheckBoxState { IsOn = command.IsOn }, new Toggled(command.IsOn));

        [On]
        public Task Other(Unrelated _) => Task.CompletedTask;

        protected override View Render(SampleCheckBoxState state) => throw new NotSupportedException();
    }

    internal sealed class SampleWithoutCommands : Screen<SampleCheckBoxState>
    {
        protected override View Render(SampleCheckBoxState state) => throw new NotSupportedException();
    }

    internal sealed class StatelessSample : Screen<StatelessSample.Empty>
    {
        internal sealed class Empty;

        [On]
        public Task Toggle(Toggle _) => Task.CompletedTask;

        protected override View Render(Empty state) => throw new NotSupportedException();
    }
}
