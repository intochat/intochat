namespace IntoChat.Cloud.Circle.Server;

// D-09 lifetimes and the T-10-03 enrollment limits. Bound from IntoChatOS:Modules:circle-server:Options.
public sealed class CircleServerOptions
{
    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshLifetime { get; set; } = TimeSpan.FromDays(30);
    public int EnrollPerAddress { get; set; } = 10;
    public TimeSpan EnrollPerAddressWindow { get; set; } = TimeSpan.FromMinutes(10);
    public int EnrollPerCircle { get; set; } = 30;
    public TimeSpan EnrollPerCircleWindow { get; set; } = TimeSpan.FromHours(1);
}
