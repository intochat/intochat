// os-only: os external-call metering metadata (Funding, CostSubject).
using DigitalBrain.Capabilities.Compute;

namespace IntoChat.OS.Framework.Modules.Metering;

public sealed record ExternalCallMetadata(string Operation, Funding Funding, CostSubject? Subject, string InvocationId);
