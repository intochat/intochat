using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record OperationIsDeclared(string Operation, string[] Operations, string App) : Rule("apps.operation")
{
    public override bool Broken => !Operations.Contains(Operation, StringComparer.Ordinal);
    public override string Message => $"{Operation} is not an operation of {App}. It offers: {string.Join(", ", Operations)}.";
}
