using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Core;
using IntoChat.OS.Modules.UI.Tabs;

namespace IntoChat.OS.Tests.Unit.Modules.UI;

public sealed class UiNeuronBaseFacts
{
    [Fact]
    public async Task TabsNeuronInheritsWinFormsStylePointerFocusAndStyleLifecycle()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);

        var toggle = os.Get<ITabs>(new NeuronId(new BrainId("test"), "ui.tabs", "wifi"));

        var configured = await toggle.ConfigureInteraction(
            subscribedEvents: UiEventMask.Hover | UiEventMask.Press | UiEventMask.Focus,
            style: new UiStyleRef(
                Tone: UiSurfaceTone.Accent,
                Typography: UiTypeRole.Label,
                Elevation: UiElevation.Subtle,
                Icon: "wifi",
                Motion: UiMotionPreset.Spring),
            motion: UiMotionPreset.Spring);

        Assert.Equal(UiEventMask.Hover | UiEventMask.Press | UiEventMask.Focus, configured.SubscribedEvents);
        Assert.Equal(UiMotionPreset.Spring, configured.Motion);
        Assert.Equal("wifi", configured.Style?.Icon);

        var entered = await toggle.OnPointerEnter(new UiPointerPayload(12, 8, 0, "mouse"));
        Assert.True(entered.IsHovered);
        Assert.False(entered.IsPressed);

        var pressed = await toggle.OnPointerDown(new UiPointerPayload(12, 8, 1, "mouse"));
        Assert.True(pressed.IsHovered);
        Assert.True(pressed.IsPressed);

        var released = await toggle.OnPointerUp(new UiPointerPayload(12, 8, 0, "mouse"));
        Assert.True(released.IsHovered);
        Assert.False(released.IsPressed);

        var left = await toggle.OnPointerLeave(new UiPointerPayload(120, 40, 0, "mouse"));
        Assert.False(left.IsHovered);
        Assert.False(left.IsPressed);

        var focused = await toggle.OnFocusChanged(true);
        Assert.True(focused.IsFocused);

        var blurred = await toggle.OnFocusChanged(false);
        Assert.False(blurred.IsFocused);
    }
}
