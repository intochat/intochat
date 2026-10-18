namespace IntoChat.OS.Microsoft.CSharp.Runtime.Signals;

[GenerateSerializer, Alias("microsoft.csharp.run-outcome")]
public enum RunOutcome { Succeeded, Failed, Dropped, Skipped }
