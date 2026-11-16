using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

internal sealed class PolicyWriter(
    IPolicyModel model,
    INeuronProxies brain,
    TimeProvider clock,
    ILogger<PolicyWriter> logger)
{
    public const int MaxRetries = 1;
    public const int MaxRequestLength = 1000;

    /// <summary>The most characters of a refusal reason shown to the person; longer or multi-line text is cut here.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>How long one model completion may take before the sentence is refused.</summary>
    public static readonly TimeSpan ModelBudget = TimeSpan.FromSeconds(60);

    public async Task<ProposalOutcome> Write(string brainId, string sentence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        if (sentence.Length > MaxRequestLength)
        {
            throw new ArgumentException($"Describe the promise in at most {MaxRequestLength} characters.", nameof(sentence));
        }

        var policies = brain.Get<IPolicies>(NeuronId.Parse(IPolicies.Key(brainId)));
        var actions = await policies.Actions(ct);
        var prompt = PolicyPrompts.Author(actions, sentence);

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string reply;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ModelBudget);
                reply = await model.Complete(brainId, PolicyPrompts.System, prompt, budget.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("PolicyWriter model did not answer within {Budget} for brain {BrainId}", ModelBudget, brainId);
                return Refused("the model did not answer in time");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The provider's message is for the log, never for the person.
                logger.LogWarning(error, "PolicyWriter model failed for brain {BrainId}", brainId);
                return Refused("the model did not answer");
            }

            if (Refusal(reply) is { } reason)
            {
                logger.LogInformation("PolicyWriter refused sentence for brain {BrainId}: {Reason}", brainId, reason);
                return Refused(reason);
            }

            PolicyDraft draft;
            try
            {
                draft = Creator.Parse<PolicyDraft>(reply);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                var problem = $"grammar: invalid JSON policy row ({ex.Message})";
                if (attempt < MaxRetries)
                {
                    prompt = PolicyPrompts.Retry(reply, problem);
                    continue;
                }

                logger.LogInformation(ex, "PolicyWriter exhausted retries on invalid JSON for brain {BrainId}", brainId);
                return Refused(problem);
            }

            var actionName = draft.Action?.Trim() ?? string.Empty;
            var declared = actions.FirstOrDefault(a => string.Equals(a.Name, actionName, StringComparison.Ordinal));
            if (declared is null)
            {
                var unknown = $"unknown action '{actionName}'";
                logger.LogInformation("PolicyWriter refused undeclared action {Action} for brain {BrainId}", actionName, brainId);
                return Refused(unknown);
            }

            PolicyRule rule;
            try
            {
                var ruleId = Guid.NewGuid().ToString("N")[..12];
                rule = PolicyGrammar.Parse(
                    ruleId,
                    declared.Name,
                    draft.Measure ?? string.Empty,
                    draft.Window ?? string.Empty,
                    draft.Limit ?? -1m,
                    draft.Condition?.Fact,
                    draft.Condition?.Op,
                    draft.Condition?.Value,
                    declared,
                    clock);
            }
            catch (PolicyGrammarException ex)
            {
                if (attempt < MaxRetries)
                {
                    prompt = PolicyPrompts.Retry(reply, ex.Message);
                    continue;
                }

                logger.LogInformation(ex, "PolicyWriter exhausted retries on grammar error for brain {BrainId}", brainId);
                return Refused(ex.Message);
            }

            return await policies.Propose(rule, ct);
        }

        return Refused("grammar: unable to parse policy rule.");
    }

    // Every refusal reason is one bounded line: model text and parser messages alike are cut before they reach the person.
    private static ProposalOutcome Refused(string reason) => new(ProposalStatus.Refused, null, string.Empty, null, Bound(reason));

    internal static string Bound(string reason)
    {
        var line = reason.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxReasonLength ? line : line[..MaxReasonLength].TrimEnd() + "\u2026";
    }

    internal static string? Refusal(string reply)
    {
        var line = reply
            .Replace("```", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (line is null || !line.StartsWith(PolicyPrompts.Refusal, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var reason = line[PolicyPrompts.Refusal.Length..].Trim();
        return reason.Length > 0 ? reason : "the model refused the policy request";
    }

    private sealed record PolicyDraft(
        string? Action,
        string? Measure,
        string? Window,
        decimal? Limit,
        PolicyConditionDraft? Condition);

    private sealed record PolicyConditionDraft(
        string? Fact,
        string? Op,
        string? Value);
}
