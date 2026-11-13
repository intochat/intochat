using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Notifications;

public sealed class NotificationsOptions : IModuleOptions
{
    [Setting]
    public int MaxItems { get; set; } = 200;

    public void Validate()
    {
        if (MaxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxItems), MaxItems, "MaxItems must be positive.");
        }
    }
}
