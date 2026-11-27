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

    [Fact]
    public void Worker_dtos_round_trip_with_web_wire_shape()
    {
        var deployReq = new DeployWorkerRequest("brain-1", "demo", "1.0.0", "sha256:abc", "AQID", "{}", "{\"k\":1}", "pipe://edge", "tok");
        var deployReqJson = JsonSerializer.Serialize(deployReq, SandboxEdgeJsonContext.Default.DeployWorkerRequest);
        Assert.Equal(JsonSerializer.Serialize(deployReq, JsonSerializerOptions.Web), deployReqJson);
        Assert.Equal(deployReq, JsonSerializer.Deserialize(deployReqJson, SandboxEdgeJsonContext.Default.DeployWorkerRequest));

        var deployResp = new DeployWorkerResponse(
            "brain-1", "demo", "1.0.0", "sha256:abc", "Demo", "Summary",
            [new WorkerCommandDescriptor("run", "Runs demo")], ["sig.a"], ["0 * * * *"], "{\"title\":\"Demo\"}", "{}");
        var deployRespJson = JsonSerializer.Serialize(deployResp, SandboxEdgeJsonContext.Default.DeployWorkerResponse);
        Assert.Equal(JsonSerializer.Serialize(deployResp, JsonSerializerOptions.Web), deployRespJson);
        var deployRespDecoded = JsonSerializer.Deserialize(deployRespJson, SandboxEdgeJsonContext.Default.DeployWorkerResponse);
        Assert.NotNull(deployRespDecoded);
        Assert.Equal("demo", deployRespDecoded.AppKey);
        Assert.Single(deployRespDecoded.Commands);
        Assert.Equal("run", deployRespDecoded.Commands[0].Name);

        var dispatchReq = new DispatchWorkerRequest("brain-1", "demo", "command", "run", "{\"x\":1}", "{}", null, "pipe://edge", "tok");
        var dispatchReqJson = JsonSerializer.Serialize(dispatchReq, SandboxEdgeJsonContext.Default.DispatchWorkerRequest);
        Assert.Equal(JsonSerializer.Serialize(dispatchReq, JsonSerializerOptions.Web), dispatchReqJson);
        Assert.Equal(dispatchReq, JsonSerializer.Deserialize(dispatchReqJson, SandboxEdgeJsonContext.Default.DispatchWorkerRequest));

        var dispatchResp = new DispatchWorkerResponse(true, "42", "{\"n\":1}", null, null);
        var dispatchRespJson = JsonSerializer.Serialize(dispatchResp, SandboxEdgeJsonContext.Default.DispatchWorkerResponse);
        Assert.Equal(JsonSerializer.Serialize(dispatchResp, JsonSerializerOptions.Web), dispatchRespJson);
        Assert.Equal(dispatchResp, JsonSerializer.Deserialize(dispatchRespJson, SandboxEdgeJsonContext.Default.DispatchWorkerResponse));

        var unloadReq = new UnloadWorkerRequest("brain-1", "demo");
        var unloadReqJson = JsonSerializer.Serialize(unloadReq, SandboxEdgeJsonContext.Default.UnloadWorkerRequest);
        Assert.Equal(unloadReq, JsonSerializer.Deserialize(unloadReqJson, SandboxEdgeJsonContext.Default.UnloadWorkerRequest));

        var unloadResp = new UnloadWorkerResponse(true, "brain-1", "demo");
        var unloadRespJson = JsonSerializer.Serialize(unloadResp, SandboxEdgeJsonContext.Default.UnloadWorkerResponse);
        Assert.Equal(unloadResp, JsonSerializer.Deserialize(unloadRespJson, SandboxEdgeJsonContext.Default.UnloadWorkerResponse));
    }

    [Fact]
    public void Unit_host_frames_omit_null_fields_and_round_trip()
    {
        var req = new UnitHostFrameRequest("frame-1", "ping");
        var reqJson = JsonSerializer.Serialize(req, UnitHostFrameJsonContext.Default.UnitHostFrameRequest);
        Assert.Equal("""{"id":"frame-1","op":"ping"}""", reqJson);

        var respJson = """{"id":"frame-1","ok":true,"processId":1234}""";
        var resp = JsonSerializer.Deserialize(respJson, UnitHostFrameJsonContext.Default.UnitHostFrameResponse);
        Assert.NotNull(resp);
        Assert.Equal("frame-1", resp.Id);
        Assert.True(resp.Ok);
        Assert.Equal(1234, resp.ProcessId);
    }
}
