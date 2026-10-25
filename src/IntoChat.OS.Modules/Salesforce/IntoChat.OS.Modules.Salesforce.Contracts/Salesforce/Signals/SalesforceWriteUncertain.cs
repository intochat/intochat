using IntoChat.OS;

namespace IntoChat.OS.Modules.Salesforce.Signals;

[GenerateSerializer, Alias("salesforce.write-uncertain")]
public sealed record SalesforceWriteUncertain([property: Id(0)] string PreviewId) : Signal;
