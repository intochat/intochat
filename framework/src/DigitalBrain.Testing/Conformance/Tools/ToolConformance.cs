using System.Text.Json;
using DigitalBrain.Capabilities.Tools;
using Xunit;

namespace DigitalBrain.Testing.Conformance.Tools;

/// <summary>A connected tool@1 subject under test.</summary>
/// <param name="Tool">The tool connection offering at least <paramref name="SampleTool"/>.</param>
/// <param name="SampleTool">Name of an allowed tool that succeeds with <paramref name="SampleArgs"/>.</param>
/// <param name="SampleArgs">JSON object arguments accepted by <paramref name="SampleTool"/>.</param>
public sealed record ToolSubject(ITool Tool, string SampleTool, JsonElement SampleArgs);

/// <summary>
/// The tool@1 conformance kit (CAP-04). A module providing <c>tool@1</c> subclasses this kit in its unit tests and
/// implements <see cref="Connect"/>.
/// </summary>
/// <typeparam name="TTool">The tool implementation under test.</typeparam>
public abstract class ToolConformance<TTool> where TTool : ITool
{
    /// <summary>Every fact's timeout in milliseconds.</summary>
    protected const int Timeout = 30_000;

    /// <summary>Creates a connected <see cref="ToolSubject"/> for one fact.</summary>
    protected abstract Task<ToolSubject> Connect(CancellationToken ct);

    /// <summary>A tool name that is not offered by the subject.</summary>
    protected virtual string MissingTool() => "kit_missing_tool_never_registered";

    /// <summary>List returns at least the sample tool with non-empty names.</summary>
    [Fact(Timeout = Timeout)]
    public async Task ListReturnsSampleTool()
    {
        var subject = await Connect(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var tools = await subject.Tool.List().ConfigureAwait(false);
        Assert.NotEmpty(tools);
        Assert.All(tools, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Contains(subject.SampleTool, tools);
    }

    /// <summary>Running the sample tool with valid object arguments returns a defined JSON element.</summary>
    [Fact(Timeout = Timeout)]
    public async Task RunSampleToolReturnsJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await Connect(ct).ConfigureAwait(false);
        var result = await subject.Tool.Run(subject.SampleTool, subject.SampleArgs, ct).ConfigureAwait(false);
        Assert.NotEqual(JsonValueKind.Undefined, result.ValueKind);
    }

    /// <summary>Running an unknown tool is refused.</summary>
    [Fact(Timeout = Timeout)]
    public async Task RunUnknownToolThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await Connect(ct).ConfigureAwait(false);
        using var empty = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<Exception>(() => subject.Tool.Run(MissingTool(), empty.RootElement.Clone(), ct)).ConfigureAwait(false);
    }

    /// <summary>Running a tool with non-object arguments is refused.</summary>
    [Fact(Timeout = Timeout)]
    public async Task RunNonObjectArgumentsThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var subject = await Connect(ct).ConfigureAwait(false);
        using var array = JsonDocument.Parse("[1, 2]");
        await Assert.ThrowsAnyAsync<Exception>(() => subject.Tool.Run(subject.SampleTool, array.RootElement.Clone(), ct)).ConfigureAwait(false);
    }
}
