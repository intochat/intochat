namespace DigitalBrain.Confirmation;

/// <summary>What a confirmation asks about.</summary>
public enum ConfirmationAction
{
    /// <summary>Delete an item.</summary>
    Delete,

    /// <summary>Move an item.</summary>
    Move,

    /// <summary>Copy an item.</summary>
    Copy,

    /// <summary>Rename an item.</summary>
    Rename,

    /// <summary>Replace an existing item.</summary>
    Overwrite,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>
/// A question for a person before something happens. Every text is shown as is; <see cref="TargetLabel"/> is a display name only,
/// never a path (T-07-08).
/// </summary>
/// <param name="Id">The plan id; unique per brain.</param>
/// <param name="Action">What would happen.</param>
/// <param name="Destructive">True when it cannot be undone (delete, move across connections, overwrite).</param>
/// <param name="Title">The card title, for example "Delete report.pdf?".</param>
/// <param name="Body">The card body.</param>
/// <param name="ApproveLabel">The approve button label, for example "Delete".</param>
/// <param name="DenyLabel">The deny button label, for example "Keep".</param>
/// <param name="TargetLabel">The display name of what is acted on.</param>
public sealed record ConfirmationPlan(
    string Id,
    ConfirmationAction Action,
    bool Destructive,
    string Title,
    string Body,
    string ApproveLabel,
    string DenyLabel,
    string TargetLabel)
{
    /// <summary>How long the question stays open; 5 minutes unless set (07 D-16).</summary>
    public TimeSpan Expiry { get; init; } = ConfirmationRules.DefaultExpiry;

    /// <summary>True when only a person can decide: the question is asked under every policy except <see cref="ConfirmationPolicy.Never"/>.</summary>
    public bool RequiresPerson { get; init; }

    /// <summary>
    /// What kind of question this is, for consumers that route plans (for example <see cref="SynapseInstallKind"/>); null for an untyped
    /// plan. Consumers match on this, never on the shape of <see cref="Id"/>.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>What the question is about, in the terms of <see cref="Kind"/> (the alias of a <see cref="SynapseInstallKind"/> plan); null when untyped.</summary>
    public string? Subject { get; init; }

    /// <summary>The <see cref="Kind"/> of the plan that asks a person to approve a synapse app's needs; <see cref="Subject"/> is the alias.</summary>
    public const string SynapseInstallKind = "synapse.install";

    /// <summary>The <see cref="Kind"/> of a person-only multi-option question.</summary>
    public const string AskKind = "confirmation.ask";

    /// <summary>Optional multi-option choices for an interactive <see cref="Ask"/> prompt; empty for binary approve/deny confirmations.</summary>
    public string[] Options { get; init; } = [];

    /// <summary>Optional zero-based index into <see cref="Options"/> identifying the recommended choice.</summary>
    public int? RecommendedOptionIndex { get; init; }

    /// <summary>
    /// Creates a person-only multi-option question (<see cref="RequiresPerson"/> is always <c>true</c>).
    /// </summary>
    public static ConfirmationPlan Ask(
        string id,
        string question,
        string[] options,
        int? recommended = null,
        string? body = null,
        string? targetLabel = null,
        TimeSpan? expiry = null,
        string? kind = null,
        string? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Length < 2)
        {
            throw new ArgumentException("A multi-option ask requires at least two options.", nameof(options));
        }

        var normalized = new string[options.Length];
        for (var i = 0; i < options.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(options[i]))
            {
                throw new ArgumentException("Every ask option must be non-empty.", nameof(options));
            }
            normalized[i] = options[i].Trim();
        }

        if (recommended is { } rec && (rec < 0 || rec >= normalized.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(recommended), rec, "Recommended option index must reference an entry in options.");
        }

        var defaultApprove = normalized[recommended ?? 0];
        return new ConfirmationPlan(
            Id: id.Trim(),
            Action: ConfirmationAction.Other,
            Destructive: false,
            Title: question.Trim(),
            Body: body ?? string.Join("\n", normalized.Select((opt, idx) => idx == recommended ? $"{idx + 1}. {opt} (Recommended)" : $"{idx + 1}. {opt}")),
            ApproveLabel: defaultApprove,
            DenyLabel: "Cancel",
            TargetLabel: string.IsNullOrWhiteSpace(targetLabel) ? "ask" : targetLabel.Trim())
        {
            Expiry = expiry ?? ConfirmationRules.DefaultExpiry,
            RequiresPerson = true,
            Kind = kind ?? AskKind,
            Subject = subject,
            Options = normalized,
            RecommendedOptionIndex = recommended,
        };
    }
}

/// <summary>How <see cref="IConfirmations.Confirm"/> ended.</summary>
/// <param name="PlanId">The plan id.</param>
/// <param name="Outcome">Approved, denied or expired.</param>
/// <param name="Reason">Why it was not approved; <see cref="ConfirmationDenyReason.None"/> when approved.</param>
/// <param name="Skipped">True when the policy approved without asking.</param>
public sealed record ConfirmationResult(string PlanId, ConfirmationOutcome Outcome, ConfirmationDenyReason Reason, bool Skipped)
{
    /// <summary>True when approved.</summary>
    public bool Approved => Outcome == ConfirmationOutcome.Approved;

    /// <summary>Who answered; null when expired or skipped.</summary>
    public ConfirmationPrincipal? Approver { get; init; }

    /// <summary>The zero-based option index chosen by the person for a multi-option ask; null for binary confirmations or non-approved outcomes.</summary>
    public int? SelectedOptionIndex { get; init; }

    /// <summary>The option text chosen by the person for a multi-option ask; null for binary confirmations or non-approved outcomes.</summary>
    public string? SelectedOption { get; init; }
}
