using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

public sealed class AppsOptions : IModuleOptions
{
    // Model neurons the authoring agents speak through; unset falls back to the neuron's default.
    public string? AuthorModel { get; set; }
    public string? BuilderModel { get; set; }
    // The fast path's one-sentence synapse writer.
    public string? FastPathModel { get; set; }
    // Its reasoning effort (none, low, medium, high, xhigh): the cheapest by default, since the sentence-to-live
    // budget is ten seconds and thinking dominated it. Empty leaves the model's own default.
    public string? FastPathReasoning { get; set; } = "none";
    // Its output cap; on most providers it bounds reasoning and the visible reply together.
    public int FastPathMaxOutputTokens { get; set; } = 2000;

    // How long a Building status blocks a new build before it counts as a stale, dead attempt.
    public int BuildFreshForSeconds { get; set; } = 900;

    // The features folder: {owner}/{App}/app.cs. The host loads it at boot and lists it in the
    // Marketplace; a relative path resolves against the host's base directory. Unset: no features.
    public string? Features { get; set; }

    public void Validate()
    {
        if (BuildFreshForSeconds < 0) { throw new InvalidOperationException("BuildFreshForSeconds cannot be negative."); }
        if (FastPathReasoning is { Length: > 0 } reasoning && reasoning.ToLowerInvariant() is not ("none" or "low" or "medium" or "high" or "xhigh"))
        { throw new InvalidOperationException("FastPathReasoning must be none, low, medium, high or xhigh."); }
        if (FastPathMaxOutputTokens <= 0) { throw new InvalidOperationException("FastPathMaxOutputTokens must be positive."); }
    }
}
