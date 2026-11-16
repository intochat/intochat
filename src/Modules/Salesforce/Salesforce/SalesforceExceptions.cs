namespace IntoChat.OS.Modules.Salesforce;

[GenerateSerializer, Alias("salesforce.unavailable")]
internal sealed class SalesforceUnavailableException(string message) : InvalidOperationException(message);

[GenerateSerializer, Alias("salesforce.not-connected")]
internal sealed class SalesforceNotConnectedException(string message = "Salesforce is not connected. Reconnect Salesforce.") : InvalidOperationException(message);

[GenerateSerializer, Alias("salesforce.unreachable")]
internal sealed class SalesforceUnreachableException(string message) : InvalidOperationException(message);
