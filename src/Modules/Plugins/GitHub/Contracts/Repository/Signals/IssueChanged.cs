namespace IntoChat.OS.Modules.Microsoft.GitHub.Signals;

[DigitalBrain.Signal("github.issue-changed")]
public sealed record IssueChanged(
    int Number,
    string Action,
    string Version,
    string SubjectKey) : DigitalBrain.Signal;
