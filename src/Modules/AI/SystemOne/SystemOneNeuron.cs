using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.SystemOne;
using IntoChat.OS.Modules.AI.SystemOne.Signals;
using IntoChat.OS.Modules.AI.Venn;

namespace IntoChat.OS.Modules.AI;

// Day-1 System One: tries in-process VENN (<1ms, 0 VRAM) first, escalates only abstained
// questions to hosted TypeSafe (if configured) or the brain's local System 2 chat model,
// and feeds high-confidence escalations back into VENN so recurring decisions self-accelerate.
[Neuron("ai.systemone")]
internal sealed partial class SystemOneNeuron(
    HttpSystemOne? client = null,
    ModelCatalog? catalog = null) : DigitalBrain.Neurons.Neuron, ISystemOne
{
    private const int MaxSummaryChars = 2000;
    private const double LearnConfidenceThreshold = 0.6;
    internal const string VennModelName = "venn-local";

    [DigitalBrain.Slot(Key = "default")]
    private partial IVennSystemOne Venn { get; }

    public async Task<SystemOneResult> Ask(SystemOneRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answers = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
        var abstained = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal);
        var venn = Venn;

        // 1. Fast path: in-process VENN (<1ms, zero external dependencies).
        if (!string.IsNullOrWhiteSpace(request.State))
        {
            foreach (var (key, question) in request.Questions)
            {
                var answer = await TryAnswerWithVennAsync(venn, key, request.State, question).ConfigureAwait(true);
                if (answer is not null)
                {
                    answers[key] = answer;
                }
                else
                {
                    abstained[key] = question;
                }
            }
        }
        else
        {
            foreach (var (key, question) in request.Questions)
            {
                abstained[key] = question;
            }
        }

        var model = VennModelName;
        var inputTokens = 0;
        var outputTokens = 0;

        // 2. Escalate only abstained questions (hosted TypeSafe if ready, else local System 2 LLM).
        if (abstained.Count > 0)
        {
            var escalatedRequest = request with { Questions = abstained };
            var escalated = client is not null && await client.IsReadyAsync(ct).ConfigureAwait(true)
                ? await client.Ask(escalatedRequest, ct).ConfigureAwait(true)
                : await LocalSystemOneEscalator.TryAskAsync(catalog, Id.Brain.Value, escalatedRequest, ct).ConfigureAwait(true);

            if (escalated is not null)
            {
                model = answers.Count > 0 ? $"{VennModelName}+{escalated.Model}" : escalated.Model;
                inputTokens = escalated.InputTokens;
                outputTokens = escalated.OutputTokens;

                foreach (var (key, answer) in escalated.Answers)
                {
                    answers[key] = answer;
                    await LearnFromEscalationAsync(venn, key, request.State, answer).ConfigureAwait(true);
                }
            }
        }

        var result = new SystemOneResult(model, answers, inputTokens, outputTokens);
        var summary = request.State.Length <= MaxSummaryChars ? request.State : request.State[..MaxSummaryChars];
        await Publish(new SystemOneDecided(summary, result.Answers, Confidence(result)));
        return result;
    }

    private static async Task<SystemOneAnswer?> TryAnswerWithVennAsync(
        IVennSystemOne venn,
        string key,
        string state,
        SystemOneQuestion question)
    {
        switch (question)
        {
            case SystemOneQuestion.Choice choice when choice.Criteria.Count > 0:
                {
                    var candidates = choice.Criteria.Keys.ToArray();
                    var res = await venn.ChooseForText(state, candidates).ConfigureAwait(true);
                    if (res.Abstained || string.IsNullOrEmpty(res.ChosenAction))
                    {
                        return null;
                    }
                    return new SystemOneAnswer.Choice(res.ChosenAction, NormalizeScores(res.Scores), res.Confidence);
                }
            case SystemOneQuestion.Score:
                {
                    var res = await venn.ScoreForText(state).ConfigureAwait(true);
                    if (res.Abstained)
                    {
                        return null;
                    }
                    return new SystemOneAnswer.Score(res.PredictedScore, [], res.Confidence);
                }
            case SystemOneQuestion.Noul:
                {
                    var res = await venn.NoulForText(state, key).ConfigureAwait(true);
                    if (res.Abstained)
                    {
                        return null;
                    }
                    return new SystemOneAnswer.Noul(res.Probability);
                }
            default:
                return null;
        }
    }

    private static async Task LearnFromEscalationAsync(
        IVennSystemOne venn,
        string domain,
        string state,
        SystemOneAnswer answer)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        if (answer is SystemOneAnswer.Choice choice
            && choice.Confidence >= LearnConfidenceThreshold
            && !string.IsNullOrWhiteSpace(choice.Value))
        {
            await venn.LearnFromText(domain, state, choice.Value, choice.Confidence, autoCompile: true).ConfigureAwait(true);
        }
    }

    private static Dictionary<string, double> NormalizeScores(Dictionary<string, double> scores)
    {
        if (scores.Count == 0)
        {
            return [];
        }
        var max = scores.Values.Max();
        var exp = new Dictionary<string, double>(scores.Count, StringComparer.Ordinal);
        var sum = 0.0;
        foreach (var (k, v) in scores)
        {
            var e = Math.Exp((v - max) * 4.0);
            exp[k] = e;
            sum += e;
        }
        if (sum <= 0)
        {
            return new Dictionary<string, double>(scores, StringComparer.Ordinal);
        }
        var probs = new Dictionary<string, double>(scores.Count, StringComparer.Ordinal);
        foreach (var (k, e) in exp)
        {
            probs[k] = e / sum;
        }
        return probs;
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
        return best;
    }
}
