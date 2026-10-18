using IntoChat.OS;

namespace IntoChat.OS.Salesforce.Signals;

[GenerateSerializer, Alias("salesforce.write-prepared")]
public sealed record SalesforceWritePrepared([property: Id(0)] SalesforceWritePreview Preview) : Signal;
