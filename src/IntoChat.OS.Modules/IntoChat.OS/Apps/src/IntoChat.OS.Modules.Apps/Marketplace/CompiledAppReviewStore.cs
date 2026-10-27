using System.Security.Cryptography;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

// Ephemeral review tokens are not grants. The installer checks current ownership/capabilities and the
// durable app owner compares revision again at commit. A restart requires a fresh review.
internal sealed class CompiledAppReviewStore(TimeProvider clock)
{
    private TimeProvider Clock { get; } = clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _reviews = new(StringComparer.Ordinal);
    private const int Capacity = 4096;

    internal CompiledAppReview Create(string brain, string name, string digest, long revision, bool replaces,
        AppRequirements requires, string[] adapters, CallerContext caller)
    {
        RequirePerson(brain, caller);
        ArgumentNullException.ThrowIfNull(requires);
        ArgumentNullException.ThrowIfNull(adapters);
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
        { throw new ArgumentException("Choose an app name using lowercase letters, digits and dashes.", nameof(name)); }
        if (digest is not { Length: 64 } || digest.Any(c => !char.IsAsciiHexDigitLower(c)))
        { throw new ArgumentException("A package digest is a lowercase SHA256 value.", nameof(digest)); }
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        requires.Validated();
        lock (_gate)
        {
            var now = Clock.GetUtcNow();
            foreach (var expired in _reviews.Where(pair => pair.Value.Review.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            { _reviews.Remove(expired); }
            if (_reviews.Count >= Capacity) { throw new InvalidOperationException("Too many pending package reviews; try again after they expire."); }
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var review = new CompiledAppReview(token, digest, brain, name, revision, replaces, now.AddMinutes(10),
                Copy(requires), [.. adapters.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
            _reviews.Add(token, new(review, caller.AccountId, caller.PrincipalId));
            return Copy(review);
        }
    }

    internal CompiledAppReview Authorize(CompiledAppApproval approval, string brain, string name, long revision,
        CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(approval);
        RequirePerson(brain, caller);
        if (approval.OperationId == Guid.Empty) { throw new InvalidOperationException("Package activation requires an operation ID."); }
        lock (_gate)
        {
            if (!_reviews.TryGetValue(approval.ReviewToken, out var entry) || entry.Review.ExpiresAt <= Clock.GetUtcNow())
            { throw new InvalidOperationException("The package review expired or is unavailable; review the package again."); }
            var review = entry.Review;
            if (entry.Account != caller.AccountId || entry.Principal != caller.PrincipalId || review.Brain != brain
                || review.Name != name || review.Digest != approval.Digest)
            { throw new UnauthorizedAccessException("The approval belongs to another owner, destination or package."); }
            if (review.ExpectedRevision != revision) { throw new InvalidOperationException("The installed app changed; review the replacement again."); }
            if (!approval.TrustCode || (review.ReplacesExisting && !approval.ReplaceExisting)
                || approval.RequirementsConsent != AppConsent.Of(review.Requires))
            { throw new UnauthorizedAccessException("Accept this package's code trust, requirements and any replacement before installation."); }
            if (entry.OperationId is { } operation && operation != approval.OperationId)
            { throw new InvalidOperationException("This review already belongs to another installation operation."); }
            entry.OperationId = approval.OperationId;
            return Copy(review);
        }
    }

    private static void RequirePerson(string brain, CallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Kind != CallerKind.User || caller.StampedBy != TrustedEdge.AuthenticatedHttp || CallerContextStamper.IsModelOriginated(caller)
            || BrainScope.Create(caller.AccountId, caller.BrainId).Id != brain)
        { throw new UnauthorizedAccessException("Only a person's authenticated session can approve compiled code in its own brain."); }
    }

    private static CompiledAppReview Copy(CompiledAppReview review)
        => review with { Requires = Copy(review.Requires), HostAdapters = [.. review.HostAdapters] };

    private static AppRequirements Copy(AppRequirements requirements)
        => new(requirements.SemanticTypes is { } semantic ? [.. semantic] : null,
            requirements.Connectors is { } connectors ? [.. connectors] : null,
            requirements.Secrets is { } secrets ? [.. secrets] : null,
            requirements.Capabilities is { } capabilities ? [.. capabilities] : null);

    private sealed class Entry(CompiledAppReview review, string account, string principal)
    {
        internal CompiledAppReview Review { get; } = review;
        internal string Account { get; } = account;
        internal string Principal { get; } = principal;
        internal Guid? OperationId { get; set; }
    }
}
