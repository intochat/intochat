using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>Stateless helper to project concrete screen types without calling their constructors directly in tests.</summary>
public static class ScreenPreview
{
    public static TResult With<TScreen, TResult>(Func<TScreen, TResult> action) where TScreen : class, new()
    {
        ArgumentNullException.ThrowIfNull(action);
        return action(Activator.CreateInstance<TScreen>());
    }
}
