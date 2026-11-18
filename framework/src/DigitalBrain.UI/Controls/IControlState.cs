namespace DigitalBrain.UI.Controls;

/// <summary>What every interactive control's state has: a disabled control refuses every command with <c>control.disabled</c>.</summary>
public interface IControlState
{
    /// <summary>False refuses every command.</summary>
    bool IsEnabled { get; }
}
