using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Apps;

public sealed class AppsOptions : IModuleOptions
{
    // Model neurons the authoring agents speak through; unset falls back to the neuron's default.
    public string? AuthorModel { get; set; }
    public string? BuilderModel { get; set; }

    // How long a Building status blocks a new build before it counts as a stale, dead attempt.
    public int BuildFreshForSeconds { get; set; } = 900;

    public void Validate()
    {
        if (BuildFreshForSeconds < 0) { throw new InvalidOperationException("BuildFreshForSeconds cannot be negative."); }
    }
}
