namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

internal sealed class MovableTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}
