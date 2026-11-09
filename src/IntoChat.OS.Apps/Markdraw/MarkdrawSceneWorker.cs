using Orleans;
using Orleans.Concurrency;

namespace IntoChat.OS.Apps.Markdraw;

[StatelessWorker]
public sealed class MarkdrawSceneWorker : Grain, IMarkdrawSceneWorker
{
    public Task<MarkdrawElementSummary[]> ParseSceneElementsAsync(string content) =>
        Task.FromResult(MarkdrawSceneEngine.ParseSceneElements(content));

    public Task<(string MergedContent, string Summary)> ApplyCoDrawingPatchAsync(string baseContent, string? prompt = null, string? explicitDsl = null, string? removeIdsCsv = null) =>
        Task.FromResult(MarkdrawSceneEngine.ApplyCoDrawingPatch(baseContent, prompt, explicitDsl, removeIdsCsv));

    public Task<string> RenderSvgPreviewAsync(MarkdrawElementSummary[] elements) =>
        Task.FromResult(MarkdrawSceneEngine.RenderSvgPreview(elements));

    public Task<string> RenderExcalidrawJsonAsync(MarkdrawElementSummary[] elements) =>
        Task.FromResult(MarkdrawSceneEngine.RenderExcalidrawJson(elements));
}
