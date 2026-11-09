using Orleans;
using Orleans.CodeGeneration;
using Orleans.Concurrency;

namespace IntoChat.OS.Apps.Markdraw;

[Alias("markdraw.scene-worker")]
[Version(1)]
public interface IMarkdrawSceneWorker : IGrainWithIntegerKey
{
    [AlwaysInterleave]
    Task<MarkdrawElementSummary[]> ParseSceneElementsAsync(string content);

    [AlwaysInterleave]
    Task<(string MergedContent, string Summary)> ApplyCoDrawingPatchAsync(string baseContent, string? prompt = null, string? explicitDsl = null, string? removeIdsCsv = null);

    [AlwaysInterleave]
    Task<string> RenderSvgPreviewAsync(MarkdrawElementSummary[] elements);

    [AlwaysInterleave]
    Task<string> RenderExcalidrawJsonAsync(MarkdrawElementSummary[] elements);
}
