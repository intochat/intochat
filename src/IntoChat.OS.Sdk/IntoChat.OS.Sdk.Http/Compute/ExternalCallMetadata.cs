using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Sdk.Http.Compute;

public sealed record ExternalCallMetadata(string Operation, Funding Funding, CostSubject? Subject, string InvocationId);
