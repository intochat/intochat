using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Authoring;

[NeuronAlias(AuthorNeuron.NeuronType), DefaultNeuronType(AuthorNeuron.NeuronType)]
public interface IAuthorNeuron
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), AuthorNeuron.NeuronType, "default");

    [ReadOnlyCall]
    Task<AuthoringSpec> PrepareSpec(AuthoringRequest request, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<AuthoringVerification> VerifyBuild(AuthoringSpec spec, IReadOnlyDictionary<string, string> files, CancellationToken ct = default);

    Task<UnitBuildOutcome> ConcludeOutcome(AuthoringSpec spec, bool success, string? hash, IReadOnlyDictionary<string, string> files, string? detail = null);
}
