using System.Text;
using DigitalBrain;
using DigitalBrain.Limits;

namespace IntoChat.OS.Modules.Apps;

internal static class PolicyPrompts
{
    public const string Refusal = "REFUSE:";

    public const string System = """
        You turn a person's promise or spending/action rule into one policy row.

        Policy grammar, the whole of it:
        - Action: a name a module declared, {module}.{verb} such as gmail.draft.create, salesforce.record.write, github.pull-request.merge, mcp.tool.run, charges.charge.
        - Measure: Count or Sum (of the amount the module passes to Allow; Sum is valid only when the action declares an AmountUnit).
        - Window: Day, Week (Monday 00:00 UTC), Month, or Rolling with a TimeSpan of at least one hour (e.g. Rolling:01:00:00); None means the limit applies to each single call.
        - Limit: a non-negative decimal.
        - Condition: optional; Fact Op Value with Op in Equals, Contains, StartsWith, EndsWith over the string facts the module passes.
        - Anything else is refused by the parser with reason fragment grammar; you get at most one retry with the parser's message.

        When no declared action matches what the person asked, or the sentence cannot be expressed in this grammar, reply with one line starting with REFUSE:
        REFUSE: <short reason, e.g. no module charges money>

        Otherwise reply with JSON only, no prose and no code fences:
        {"action":"charges.charge","measure":"Sum","window":"Week","limit":300,"condition":null}
        or with a condition:
        {"action":"gmail.draft.create","measure":"Count","window":"Day","limit":5,"condition":{"fact":"To","op":"EndsWith","value":"@corp.com"}}
        """;

    public static string Author(IReadOnlyList<ActionDescriptor> actions, string sentence)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);

        var builder = new StringBuilder();
        builder.AppendLine("Declared module actions:");
        if (actions.Count == 0)
        {
            builder.AppendLine("- (none)");
        }
        else
        {
            foreach (var action in actions)
            {
                var unit = string.IsNullOrWhiteSpace(action.AmountUnit) ? "Count only (no AmountUnit)" : $"AmountUnit: {action.AmountUnit}";
                builder.AppendLine($"- {action.Name}: {action.Description} ({unit})");
            }
        }

        builder.AppendLine();
        builder.AppendLine("The person's sentence:");
        builder.Append(sentence.Trim());
        return builder.ToString();
    }

    public static string Retry(string reply, string problem)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Your previous reply:");
        builder.AppendLine(reply.Trim());
        builder.AppendLine();
        builder.AppendLine("It was rejected by the policy parser:");
        builder.AppendLine(problem);
        builder.AppendLine();
        builder.Append("Fix the error and reply with the corrected JSON object only (or REFUSE: <reason> if the request cannot be expressed).");
        return builder.ToString();
    }
}
