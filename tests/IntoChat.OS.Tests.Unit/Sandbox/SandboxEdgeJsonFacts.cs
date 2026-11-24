using System.Text.Json;
using IntoChat.OS.Kernel.Sandbox;

namespace IntoChat.OS.Tests.Unit.Sandbox;

// The edge records serialize through the source-generated context with the same camelCase wire shape the worker reads and sends.
public sealed class SandboxEdgeJsonFacts
{
    [Fact]
    public void Acks_keep_their_wire_shape()
    {
        Assert.Equal(
            """{"forwarded":true,"signal":"app.ready"}""",
            JsonSerializer.Serialize(new SandboxEdgeEndpoints.EdgeSignalAck(true, "app.ready"), SandboxEdgeJsonContext.Default.EdgeSignalAck));
        Assert.Equal(
            """{"logged":true}""",
            JsonSerializer.Serialize(new SandboxEdgeEndpoints.EdgeLogAck(true), SandboxEdgeJsonContext.Default.EdgeLogAck));
    }

    [Fact]
    public void Invoke_response_matches_the_web_shape()
    {
        var response = new SandboxEdgeEndpoints.EdgeInvokeResponse("{}", "denied");

        Assert.Equal(
            JsonSerializer.Serialize(response, JsonSerializerOptions.Web),
            JsonSerializer.Serialize(response, SandboxEdgeJsonContext.Default.EdgeInvokeResponse));
    }

    [Fact]
    public void Requests_bind_from_the_worker_payload()
    {
        var invoke = JsonSerializer.Deserialize(
            """{"contract":"IInoFiles","method":"Write","slot":null,"argsJson":"[]"}""",
            SandboxEdgeJsonContext.Default.EdgeInvokeRequest);
        var log = JsonSerializer.Deserialize(
            """{"message":"hi","timestampUtc":"2026-01-02T03:04:05+00:00"}""",
            SandboxEdgeJsonContext.Default.EdgeLogRequest);
        var signal = JsonSerializer.Deserialize(
            """{"SignalName":"s","PayloadJson":"{}"}""",
            SandboxEdgeJsonContext.Default.EdgeSignalRequest);

        Assert.Equal(new SandboxEdgeEndpoints.EdgeInvokeRequest("IInoFiles", "Write", null, "[]"), invoke);
        Assert.Equal(new SandboxEdgeEndpoints.EdgeLogRequest("hi", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)), log);
        Assert.Equal(new SandboxEdgeEndpoints.EdgeSignalRequest("s", "{}"), signal);
    }
}
