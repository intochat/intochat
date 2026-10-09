using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Revisions;

public sealed class RevisionsOptions : IModuleOptions
{
    public string LauncherUrl { get; set; } = "http://localhost:5079";
    // How long a child may take to answer /health after aspire run starts; a cold image build takes minutes.
    public TimeSpan StartBudget { get; set; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (!Uri.TryCreate(LauncherUrl, UriKind.Absolute, out _)) { throw new ArgumentException("LauncherUrl must be an absolute URL."); }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StartBudget, TimeSpan.Zero);
    }
}