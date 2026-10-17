using DigitalBrain.AI.SystemOne;
using DigitalBrain.AI.SystemOne.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.AI;

[GrainType("ai.systemone")]
internal sealed class SystemOneNeuron(HttpSystemOne client) : Neuron, ISystemOne
{
    private const int MaxSummaryChars = 2000;

    public async Task<SystemOneResult> Ask(SystemOneRequest request, CancellationToken ct = default)
    {
        var result = await client.Ask(request, ct);
        var summary = request.State.Length <= MaxSummaryChars ? request.State : request.State[..MaxSummaryChars];
        await PublishAsync(new SystemOneDecided(summary, result.Answers, Confidence(result)));
        return result;
    }

    private static double Confidence(SystemOneResult result)
    {
        var best = 0.0;
        foreach (var answer in result.Answers.Values)
        {
            var confidence = answer switch
            {
                SystemOneAnswer.Choice choice => choice.Confidence,
                SystemOneAnswer.Score score => score.Confidence,
                _ => 0.0,
            };
            if (confidence > best) { best = confidence; }
        }
        return best; ;
    }
}
