using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.AI.Aspire.Hosting;

/// <summary>
/// Local-only tuning for the Aspire-managed Ollama container, bound from <c>IntoChatOS:Ollama</c>.
/// Every tuning property is optional so default local runs stay hardware-agnostic, while machine-specific
/// profiles (such as an RTX 5080 16 GB profile) can opt in cleanly without modifying shared defaults.
/// </summary>
public sealed class OllamaContainerOptions
{
    public const string SectionName = "IntoChatOS:Ollama";

    public bool AutoContainer { get; set; } = true;

    /// <summary>Maps to <c>OLLAMA_CONTEXT_LENGTH</c> (e.g. 32768 to avoid truncating tool prompts).</summary>
    public int? ContextLength { get; set; }

    /// <summary>Maps to <c>OLLAMA_FLASH_ATTENTION</c> (<c>1</c> / <c>0</c>).</summary>
    public bool? FlashAttention { get; set; }

    /// <summary>Maps to <c>OLLAMA_KV_CACHE_TYPE</c> (e.g. <c>q8_0</c>, <c>q4_0</c>, <c>f16</c>).</summary>
    public string? KvCacheType { get; set; }

    /// <summary>Maps to <c>OLLAMA_KEEP_ALIVE</c> (e.g. <c>30m</c> to keep models hot in VRAM for voice turns).</summary>
    public string? KeepAlive { get; set; }

    /// <summary>Maps to <c>OLLAMA_NUM_PARALLEL</c> (concurrent inference slots).</summary>
    public int? NumParallel { get; set; }

    public static OllamaContainerOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new OllamaContainerOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }

    internal IResourceBuilder<OllamaResource> ApplyTo(IResourceBuilder<OllamaResource> ollama)
    {
        ArgumentNullException.ThrowIfNull(ollama);
        if (ContextLength is > 0 and var contextLength)
        {
            ollama = ollama.WithEnvironment("OLLAMA_CONTEXT_LENGTH", contextLength.ToString(CultureInfo.InvariantCulture));
        }

        if (FlashAttention is { } flashAttention)
        {
            ollama = ollama.WithEnvironment("OLLAMA_FLASH_ATTENTION", flashAttention ? "1" : "0");
        }

        if (!string.IsNullOrWhiteSpace(KvCacheType))
        {
            ollama = ollama.WithEnvironment("OLLAMA_KV_CACHE_TYPE", KvCacheType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(KeepAlive))
        {
            ollama = ollama.WithEnvironment("OLLAMA_KEEP_ALIVE", KeepAlive.Trim());
        }

        if (NumParallel is > 0 and var numParallel)
        {
            ollama = ollama.WithEnvironment("OLLAMA_NUM_PARALLEL", numParallel.ToString(CultureInfo.InvariantCulture));
        }

        return ollama;
    }
}
