using System.Reflection;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Units all the way: DigitalBrain.UI.View is the one UI tree. The Flutter module keeps the view, one field neuron per view field,
// the hosted surfaces (chat, composer, web browser, viewport) and its non-widget neurons; the per-widget neurons are gone. The
// widget neurons still listed under "Pending" are read or written by a non-UI module and leave with their last user.
public sealed class OneUiPathFacts
{
    private static readonly string[] Kept =
    [
        "view", "ui.field", "webbrowser", "viewport",
        // Non-UI neurons of the module: a chat's stored projection (rendered as view nodes by its app) and the shell's sources.
        "chat",
        "chat-index", "ui.compute", "ui.problem-reports", "ui.space", "ui.workspace",
    ];

    private static readonly string[] Pending = ["text", "button", "layout", "collection", "infobar", "card", "image", "chart", "table", "person", "form", "ui.binding"];

    [Fact]
    public void NoNeuronOutsideTheKeptUiKinds()
    {
        var aliases = ProductionArchitecture.Types
            .Where(type => type.Assembly.GetName().Name == "IntoChat.OS.Modules.Flutter")
            .Select(type => type.GetCustomAttribute<DigitalBrain.NeuronAttribute>(inherit: false)?.Type)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ui.field", aliases);
        var outside = aliases.Except(Kept).Except(Pending).Order(StringComparer.Ordinal).ToArray();
        Assert.True(outside.Length == 0, "Neurons outside the kept UI kinds: " + string.Join(", ", outside));
    }
}
