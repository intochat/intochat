using DigitalBrain;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Pure synapse unit that reacts to <see cref="PullRequestChanged"/> signals, ignores drafts and
/// non-reviewable transitions, deduplicates on <c>{SubjectKey}@{HeadSha}</c> (bounded to the last 200 keys),
/// and emits <see cref="ReviewRequested"/>.
/// </summary>
internal static class PrReviewTrigger
{
    public const int MaxSeenKeys = 200;

    public static Reaction<SeenPrHeads> OnChanged(PullRequestChanged pr, SeenPrHeads seen)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var current = seen ?? new SeenPrHeads();

        if (pr.IsDraft)
        {
            return Reaction<SeenPrHeads>.Of(current);
        }

        if (pr.Kind is not (PullRequestKind.Opened or PullRequestKind.Updated or PullRequestKind.ReadyForReview))
        {
            return Reaction<SeenPrHeads>.Of(current);
        }

        if (string.IsNullOrWhiteSpace(pr.SubjectKey) || string.IsNullOrWhiteSpace(pr.HeadSha))
        {
            return Reaction<SeenPrHeads>.Of(current);
        }

        var key = $"{pr.SubjectKey}@{pr.HeadSha}";
        var existing = current.Keys ?? [];
        if (existing.Contains(key, StringComparer.Ordinal))
        {
            return Reaction<SeenPrHeads>.Of(current);
        }

        string[] nextKeys = existing.Length >= MaxSeenKeys
            ? [.. existing.Skip(existing.Length - MaxSeenKeys + 1), key]
            : [.. existing, key];

        return Reaction<SeenPrHeads>.Of(
            new SeenPrHeads(nextKeys),
            new ReviewRequested(pr.SubjectKey, pr.Number, pr.HeadSha));
    }
}
