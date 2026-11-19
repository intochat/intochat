using DigitalBrain.UI;

namespace DigitalBrain.Testing.Tests.Unit.Views;

// Props hash their items in order, so two lists of one length with different items do not collide by construction.
public sealed class PropsHashFacts
{
    [Fact]
    public void Two_list_boxes_of_one_length_with_different_items_hash_differently()
    {
        var a = new ListBoxProps([new ListBoxItem("a", "Alpha"), new ListBoxItem("b", "Beta")]);
        var b = new ListBoxProps([new ListBoxItem("a", "Alpha"), new ListBoxItem("c", "Gamma")]);

        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Two_tab_strips_with_the_same_items_in_another_order_hash_differently()
    {
        var a = new TabStripProps([new TabStripItem("a", "A"), new TabStripItem("b", "B")]);
        var b = new TabStripProps([new TabStripItem("b", "B"), new TabStripItem("a", "A")]);

        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equal_props_hash_alike()
    {
        Assert.Equal(new ListBoxProps([new ListBoxItem("a", "Alpha")]).GetHashCode(), new ListBoxProps([new ListBoxItem("a", "Alpha")]).GetHashCode());
    }
}

public sealed class FakeNeuronContextClassFacts
{
    private sealed class Probe;

    [Fact]
    public void ClassOf_returns_the_registered_type_and_null_for_an_unknown_one()
    {
        var context = new FakeNeuronContext(new NeuronId(new BrainId("owner"), "test.self", "default")).RegisterClass("test.probe", typeof(Probe));

        Assert.Equal(typeof(Probe), context.ClassOf("test.probe"));
        Assert.Null(context.ClassOf("test.other"));
    }
}
