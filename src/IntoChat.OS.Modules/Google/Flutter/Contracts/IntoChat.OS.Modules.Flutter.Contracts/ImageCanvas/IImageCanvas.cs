using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.ImageCanvas;

[NeuronAlias("imagecanvas"), DefaultNeuronType(UIVocabulary.ImageCanvasType)]
public interface IImageCanvas
{
    Task Set(ImageCanvasDefinition definition, long expectedRevision);
    Task RequestEdit(ImageRecipe recipe, long expectedDocumentRevision, string operationId);
    [DigitalBrain.ReadOnlyCall] Task<ImageCanvasState> Read();
}

[GenerateSerializer, Alias("ui.imagecanvas-definition")]
public sealed record ImageCanvasDefinition([property: Id(0)] string AssetId, [property: Id(1)] int Width, [property: Id(2)] int Height, [property: Id(3)] ImageRecipe Recipe, [property: Id(4)] long DocumentRevision);

[GenerateSerializer, Alias("ui.imagecanvas-state")]
[ExportableState]
public sealed class ImageCanvasState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public ImageCanvasDefinition Definition { get; set; } = new("", 0, 0, new(), 0);
}

[Signal("ui.imagecanvas-changed")]
public sealed record ImageCanvasChanged(string Name, long Revision) : DigitalBrain.Signal;

[Signal("ui.imagecanvas-edit-requested")]
public sealed record ImageCanvasEditRequested(string Name, ImageRecipe Recipe, long DocumentRevision, string OperationId) : DigitalBrain.Signal;
