namespace IntoChat.OS.Sdk.Validation;

public sealed record ValidationIssue(string Path, string Code, string Message);
