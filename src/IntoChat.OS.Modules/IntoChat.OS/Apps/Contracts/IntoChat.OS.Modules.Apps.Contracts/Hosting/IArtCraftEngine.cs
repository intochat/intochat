namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Contracts.NeuronAlias("apps.artcraft-engine"), DigitalBrain.Contracts.DefaultNeuronType("apps.artcraft-engine")]
public interface IArtCraftEngine
{
    Task<ArtCraftEngineResult> ExecutePhotoCraft(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentDocument,
        string executionMode);

    Task<ArtCraftEngineResult> ExecuteFilmCraft(
        string? instructionOrCommand,
        string? explicitParamsJson,
        string? currentSequence,
        string executionMode);
}

[GenerateSerializer, Alias("apps.artcraft.result")]
public sealed record ArtCraftEngineResult(
    [property: Id(0)] string App,
    [property: Id(1)] string ExecutionMode,
    [property: Id(2)] string CommandId,
    [property: Id(3)] string CommandTitle,
    [property: Id(4)] string ParametersJson,
    [property: Id(5)] string InputDocument,
    [property: Id(6)] string OutputDocument,
    [property: Id(7)] string CliInvocation,
    [property: Id(8)] string Summary,
    [property: Id(9)] string PreviewDataUrl,
    [property: Id(10)] string[] LayersOrTracks,
    [property: Id(11)] bool IsValidCommand = true,
    [property: Id(12)] string? OutputNeuronUri = null);
