using System.Runtime.CompilerServices;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.AI;

namespace DigitalBrain.AI.Metering;

// Prices the provider's reported token classes through the kernel's cost meter, one set of rows
// per raw provider call, immediately after the response reports usage. It sits at the innermost
// pipeline layer so it measures a call exactly once; streaming usage is accumulated from the
// updates just as ChatResponseExtensions.ToChatResponse(IEnumerable{ChatResponseUpdate})
// aggregates it, so a turn is never counted twice.
internal sealed class MeteringChatClient(
    IChatClient innerClient, ICostMeter meter, Funding funding, string model)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await RecordAsync(response.Usage, response.ResponseId, response.ModelId, options?.ModelId, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        UsageDetails? usage = null;
        string? responseId = null;
        string? reportedModel = options?.ModelId;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                if (update.ModelId is { Length: > 0 } updateModel) { reportedModel = updateModel; }
                if (update.ResponseId is { Length: > 0 } updateId) { responseId = updateId; }
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent { Details: { } details }) { (usage ??= new()).Add(details); }
                }
                yield return update;
            }
        }
        finally
        {
            // A stream abandoned before its final chunk still prices the usage that arrived.
            await RecordAsync(usage, responseId, reportedModel, options?.ModelId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The unit is million tokens, the price list's unit; the cost id is the response's own identity
    // plus the token class, so a prompt rerun prices fresh rows instead of replaying old ones. The token
    // classes are independent rows, so they go to the meter together: the response waits for one meter
    // round trip, not one per class.
    private async Task RecordAsync(UsageDetails? usage, string? responseId, string? responseModel, string? optionModel, CancellationToken cancellationToken)
    {
        if (usage is null) { return; }
        var id = responseId is { Length: > 0 } responseIdText ? responseIdText : Guid.NewGuid().ToString("N");
        var item = responseModel ?? optionModel ?? model;
        (string Class, long? Tokens)[] classes =
        [
            ("input", usage.InputTokenCount),
            ("cached", usage.CachedInputTokenCount),
            ("reasoning", usage.ReasoningTokenCount),
            ("output", usage.OutputTokenCount),
        ];
        try
        {
            await Task.WhenAll(classes.Where(entry => entry.Tokens is not null)
                .Select(entry => RecordAsync($"{item}:{entry.Class}", entry.Tokens!.Value, $"{id}:{entry.Class}", cancellationToken))).ConfigureAwait(false);
        }
        catch { /* Metering is an observer: a ledger fault must never fail the model call. */ }
    }

    private Task RecordAsync(string item, long tokens, string costId, CancellationToken cancellationToken)
        => meter.RecordAsync(CostKind.Model, item, tokens / 1_000_000m, "mtok", funding, costId, cancellationToken).AsTask();
}