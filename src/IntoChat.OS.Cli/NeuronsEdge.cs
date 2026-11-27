using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Apps.Runs;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Cli;

// The kernel's gRPC neuron channel (Neurons.Call in the kernel's neurons.proto) over gRPC-Web, acting as the app "cli" for the
// person the proof names. The CLI needs one method of two small messages, so it marshals them itself rather than carrying the
// protobuf runtime and a second copy of the generated types.
public sealed class NeuronsEdge : IDisposable
{
    private static readonly Method<NeuronCall, byte[]> CallMethod = new(MethodType.Unary, "intochat.os.Neurons", "Call",
        Marshallers.Create<NeuronCall>(request => request.Encode(), _ => throw new NotSupportedException()),
        Marshallers.Create(_ => throw new NotSupportedException(), NeuronCall.DecodeReply));

    private readonly GrpcChannel _channel;
    private readonly CallInvoker _invoker;
    private readonly Metadata _headers;
    private readonly string _brain;

    public NeuronsEdge(Uri address, string brain, CliProof? proof, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        _brain = brain;
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, handler ?? new HttpClientHandler()),
            HttpVersion = HttpVersion.Version11,
            DisposeHttpClient = true,
        });
        _invoker = _channel.CreateCallInvoker();
        _headers = new Metadata { { ActingApp.HeaderName, ShippedGrants.CliApp } };
        foreach (var (name, value) in proof?.Headers() ?? []) { _headers.Add(name, value); }
    }

    // One neuron method by its contract, key and named arguments; the reply deserialized as TReply, or default when the method
    // returned nothing.
    public async Task<TReply?> Call<TArgs, TReply>(
        Type contract,
        string key,
        string method,
        TArgs arguments,
        JsonTypeInfo<TArgs> argumentsTypeInfo,
        JsonTypeInfo<TReply> replyTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(argumentsTypeInfo);
        ArgumentNullException.ThrowIfNull(replyTypeInfo);
        var request = new NeuronCall(_brain, contract.FullName!, key, method, JsonSerializer.SerializeToUtf8Bytes(arguments, argumentsTypeInfo));
        var reply = await _invoker.AsyncUnaryCall(CallMethod, null, new CallOptions(_headers, cancellationToken: ct), request);
        return reply.Length == 0 ? default : JsonSerializer.Deserialize(reply, replyTypeInfo);
    }

    public void Dispose() => _channel.Dispose();

    internal sealed record NeuronCall(string BrainId, string Contract, string Key, string Method, byte[] Arguments)
    {
        // Fields 1 to 5 of intochat.os.CallRequest, each length-delimited (wire type 2).
        internal byte[] Encode()
        {
            var buffer = new ArrayBufferWriter<byte>();
            Field(buffer, 1, Encoding.UTF8.GetBytes(BrainId));
            Field(buffer, 2, Encoding.UTF8.GetBytes(Contract));
            Field(buffer, 3, Encoding.UTF8.GetBytes(Key));
            Field(buffer, 4, Encoding.UTF8.GetBytes(Method));
            Field(buffer, 5, Arguments);
            return buffer.WrittenSpan.ToArray();
        }

        // Field 1 of intochat.os.CallReply (the JSON result); every other field is skipped by its wire type.
        internal static byte[] DecodeReply(byte[] message)
        {
            var result = Array.Empty<byte>();
            var at = 0;
            while (at < message.Length)
            {
                var tag = Varint(message, ref at);
                switch (tag & 7)
                {
                    case 0: Varint(message, ref at); break;
                    case 1: at += 8; break;
                    case 5: at += 4; break;
                    case 2:
                        var length = checked((int)Varint(message, ref at));
                        if (length < 0 || at + length > message.Length) { throw new InvalidDataException("A CallReply field runs past the message."); }
                        if (tag >> 3 == 1) { result = message[at..(at + length)]; }
                        at += length;
                        break;
                    default: throw new InvalidDataException($"CallReply carries an unknown wire type {tag & 7}.");
                }
            }
            return result;
        }

        private static void Field(ArrayBufferWriter<byte> buffer, int number, byte[] value)
        {
            if (value.Length == 0) { return; }
            Write(buffer, (ulong)((number << 3) | 2));
            Write(buffer, (ulong)value.Length);
            buffer.Write(value);
        }

        private static void Write(ArrayBufferWriter<byte> buffer, ulong value)
        {
            while (value >= 0x80)
            {
                buffer.Write([(byte)(value | 0x80)]);
                value >>= 7;
            }
            buffer.Write([(byte)value]);
        }

        private static ulong Varint(byte[] message, ref int at)
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (at >= message.Length) { throw new InvalidDataException("A CallReply varint runs past the message."); }
                var current = message[at++];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0) { return value; }
            }
            throw new InvalidDataException("A CallReply varint is longer than 64 bits.");
        }
    }
}

public sealed record ListRunsArgs(int Limit);
public sealed record StartAppRunArgs(AppTrigger Trigger, string App, string Name, string? PayloadJson);
public sealed record StartWorkflowArgs(string Workflow, string Input);
public sealed record CancelRunArgs(string Reason);
public sealed record EmptyArgs;
public sealed record ApproveConfirmationArgs(bool Approve);
public sealed record LinkUriArgs(string Uri);
public sealed record LinkServerCodeArgs(string Server, string Code);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ListRunsArgs))]
[JsonSerializable(typeof(StartAppRunArgs))]
[JsonSerializable(typeof(StartWorkflowArgs))]
[JsonSerializable(typeof(CancelRunArgs))]
[JsonSerializable(typeof(EmptyArgs))]
[JsonSerializable(typeof(ApproveConfirmationArgs))]
[JsonSerializable(typeof(LinkUriArgs))]
[JsonSerializable(typeof(LinkServerCodeArgs))]
[JsonSerializable(typeof(RunView[]))]
[JsonSerializable(typeof(AppRunView))]
[JsonSerializable(typeof(WorkflowRunView))]
[JsonSerializable(typeof(CodeOrchestrationView))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class CliJsonContext : JsonSerializerContext;
