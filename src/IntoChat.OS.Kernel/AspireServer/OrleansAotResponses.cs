using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Identity.Contracts.Entries;

#nullable disable

namespace IntoChat.OS.Kernel.AspireServer;

internal static class OrleansAotResponses
{
    public static void Register(IServiceCollection services)
    {
        AddEnum<DigitalBrain.Graph.GraphNodeKind>(services);
        AddEnum<DigitalBrain.Graph.GraphEdgeKind>(services);
        AddEnum<DigitalBrain.Graph.GraphLevel>(services);
        AddEnum<IntoChat.Sdk.FolderAccess>(services);
        AddEnum<IntoChat.Sdk.NotificationPriority>(services);
        AddEnum<EntryKind>(services);
        AddEnum<IntoChat.OS.Modules.Identity.Contracts.Codes.CodePurpose>(services);
        AddEnum<IntoChat.OS.Host.Modules.ModuleState>(services);
        AddEnum<IntoChat.OS.Host.ServiceDefaults.BindKind>(services);
        AddEnum<IntoChat.OS.Kernel.Composition.Registry.RegistryTier>(services);
        AddEnum<IntoChat.OS.UI.StatusKind>(services);
        AddEnum<IntoChat.OS.Modules.AI.Agents.AgentFailure>(services);
        AddEnum<IntoChat.OS.Modules.AI.Agents.AgentRunStatus>(services);
        AddEnum<IntoChat.OS.Modules.AI.GroupChat.GroupChatStatus>(services);
        AddEnum<IntoChat.OS.Modules.AI.Inference.CapabilitySupport>(services);
        AddEnum<IntoChat.OS.Modules.AI.Media.MediaOperation>(services);
        AddEnum<IntoChat.OS.Modules.AI.Memory.EdgeDirection>(services);
        AddEnum<IntoChat.OS.Modules.AI.Memory.MemoryTrust>(services);
        AddEnum<IntoChat.OS.Modules.AI.Metering.MeterKind>(services);
        AddEnum<IntoChat.OS.Modules.AI.Models.AiProvider>(services);
        AddEnum<IntoChat.OS.Modules.AI.Models.Where>(services);
        AddEnum<IntoChat.OS.Modules.AI.Models.Abilities>(services);
        AddEnum<IntoChat.OS.Modules.AI.Models.Tier>(services);
        AddEnum<IntoChat.OS.Modules.AI.Models.Role>(services);
        AddEnum<IntoChat.OS.Modules.AI.Voice.TranscriptionFormats>(services);
        AddEnum<IntoChat.OS.Modules.ArtCraft.ArtCraftCapabilities>(services);
        AddEnum<IntoChat.OS.Modules.Apps.Approvals.ApprovalKind>(services);
        AddEnum<AppStatus>(services);
        AddEnum<AppInstallStatus>(services);
        AddEnum<PresenceState>(services);
        AddEnum<FocusTone>(services);
        AddEnum<IntoChat.OS.Modules.Circles.RedeemResult>(services);
        AddEnum<IntoChat.OS.Modules.Devices.EnrollmentResult>(services);
        AddEnum<IntoChat.OS.Modules.Flutter.Conversations.Visibility>(services);
        AddEnum<IntoChat.OS.Modules.Devices.InstallState>(services);
        AddEnum<IntoChat.OS.Modules.Devices.Accelerator>(services);
        AddEnum<IntoChat.OS.Modules.Devices.Fit>(services);
        AddEnum<IntoChat.OS.Modules.Google.Gmail.GmailDraftOperationStatus>(services);
        AddEnum<IntoChat.OS.Modules.Google.Gmail.GmailAccountingStatus>(services);
        AddEnum<IntoChat.OS.Modules.Postgres.PostgresIsolation>(services);

        AddValue<IntoChat.OS.Runtime.Code>(services);
        AddNullableValue<IntoChat.OS.Runtime.Code>(services);
        AddValue<IntoChat.OS.Host.Primitives.ConversationId>(services);
        AddNullableValue<IntoChat.OS.Host.Primitives.ConversationId>(services);
        AddValue<IntoChat.OS.Modules.AI.Models.ModelId>(services);
        AddNullableValue<IntoChat.OS.Modules.AI.Models.ModelId>(services);
        AddValue<IntoChat.OS.Modules.Identity.Contracts.AccountId>(services);
        AddNullableValue<IntoChat.OS.Modules.Identity.Contracts.AccountId>(services);
        AddValue<IntoChat.OS.Modules.Flutter.Conversations.EntryId>(services);
        AddNullableValue<IntoChat.OS.Modules.Flutter.Conversations.EntryId>(services);
        AddValue<IntoChat.OS.Modules.Devices.RuntimeId>(services);
        AddNullableValue<IntoChat.OS.Modules.Devices.RuntimeId>(services);
        AddValue<IntoChat.OS.Host.Primitives.SafeRelativePath>(services);
        AddNullableValue<IntoChat.OS.Host.Primitives.SafeRelativePath>(services);

        Add<NeuronInstance[]>(services);
        Add<NeuronTypeHit[]>(services);
        Add<Feature[]>(services);
        Add<EntrySummary[]>(services);
        Add<DataQueryResult>(services);

        PreserveHttpAwaitable(typeof(Microsoft.AspNetCore.Http.IResult));
    }

    private static void Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult>(IServiceCollection services)
    {
        _ = ResponsePool.Get<TResult>();
        services.AddSingleton<IDeepCopier<TResult>>(static sp => sp.GetRequiredService<ICodecProvider>().GetDeepCopier<TResult>());
        services.AddSingleton<IFieldCodec<TResult>>(static sp => sp.GetRequiredService<ICodecProvider>().GetCodec<TResult>());
        services.AddSingleton<IDeepCopier<Response<TResult>>, OsResponseCopier<TResult>>();
        services.AddSingleton<IFieldCodec<Response<TResult>>, OsResponseCodec<TResult>>();
        services.AddSingleton<IActivator<Response<TResult>>, OsResponseActivator<TResult>>();
    }

    private static void AddEnum<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TEnum>(IServiceCollection services)
        where TEnum : struct, Enum
    {
        services.AddSingleton<IFieldCodec<TEnum>, OsEnumCodec<TEnum>>();
        services.AddSingleton<ShallowCopier<TEnum>>();
        services.AddSingleton<IDeepCopier<TEnum>>(static sp => sp.GetRequiredService<ShallowCopier<TEnum>>());
        services.AddSingleton<IActivator<TEnum>, OsStructActivator<TEnum>>();
        AddNullableValue<TEnum>(services);
    }

    private static void AddValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>(IServiceCollection services)
        where TValue : struct
    {
        services.AddSingleton<IFieldCodec<TValue>>(static sp => sp.GetRequiredService<ICodecProvider>().GetCodec<TValue>());
        services.AddSingleton<IDeepCopier<TValue>>(static sp => sp.GetRequiredService<ICodecProvider>().GetDeepCopier<TValue>());
        services.AddSingleton<IActivator<TValue>, OsStructActivator<TValue>>();
    }

    private static void AddNullableValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>(IServiceCollection services)
        where TValue : struct
    {
        services.AddSingleton<IFieldCodec<TValue?>>(static sp => new NullableCodec<TValue>(sp.GetRequiredService<IFieldCodec<TValue>>()));
        services.AddSingleton<IDeepCopier<TValue?>>(static sp => new NullableCopier<TValue>(sp.GetRequiredService<IDeepCopier<TValue>>()));
        services.AddSingleton<IActivator<TValue?>, OsStructActivator<TValue?>>();
    }

    internal sealed class OsStructActivator<T> : IActivator<T>
    {
        public T Create() => default;
    }

    internal sealed class OsEnumCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TEnum> : IFieldCodec<TEnum>
        where TEnum : struct, Enum
    {
        private readonly Type _codecFieldType = typeof(TEnum);

        public void WriteField<TBufferWriter>(
            ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            TEnum value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte>
        {
            if (System.Runtime.CompilerServices.Unsafe.SizeOf<TEnum>() == 1)
            {
                ByteCodec.WriteField(ref writer, fieldIdDelta, expectedType, System.Runtime.CompilerServices.Unsafe.As<TEnum, byte>(ref value), _codecFieldType);
            }
            else
            {
                Int32Codec.WriteField(ref writer, fieldIdDelta, expectedType, System.Runtime.CompilerServices.Unsafe.As<TEnum, int>(ref value), _codecFieldType);
            }
        }

        public TEnum ReadValue<TInput>(
            ref Orleans.Serialization.Buffers.Reader<TInput> reader,
            Orleans.Serialization.WireProtocol.Field field)
        {
            if (System.Runtime.CompilerServices.Unsafe.SizeOf<TEnum>() == 1)
            {
                var byteVal = ByteCodec.ReadValue(ref reader, field);
                return System.Runtime.CompilerServices.Unsafe.As<byte, TEnum>(ref byteVal);
            }

            var intVal = Int32Codec.ReadValue(ref reader, field);
            return System.Runtime.CompilerServices.Unsafe.As<int, TEnum>(ref intVal);
        }
    }

    internal sealed class OsResponseCopier<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult>(ICodecProvider codecProvider) : IDeepCopier<Response<TResult>>
    {
        private IDeepCopier<TResult> _copier;

        public Response<TResult> DeepCopy(Response<TResult> input, CopyContext context)
        {
            if (input is null) { return null; }
            var result = ResponsePool.Get<TResult>();
            var copier = _copier ??= codecProvider.GetDeepCopier<TResult>();
            result.TypedResult = copier.DeepCopy(input.TypedResult, context);
            return result;
        }
    }

    internal sealed class OsResponseCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult>(ICodecProvider codecProvider) : ResponseCodec, IFieldCodec<Response<TResult>>
    {
        private readonly Type _codecFieldType = typeof(Response<TResult>);
        private readonly Type _resultType = typeof(TResult);
        private IFieldCodec<TResult> _codec;

        private IFieldCodec<TResult> Codec => _codec ??= codecProvider.GetCodec<TResult>();

        public void WriteField<TBufferWriter>(
            ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            Response<TResult> value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            if (value.TypedResult is not null)
            {
                Codec.WriteField(ref writer, 0, _resultType, value.TypedResult);
            }
            writer.WriteEndObject();
        }

        public Response<TResult> ReadValue<TInput>(
            ref Orleans.Serialization.Buffers.Reader<TInput> reader,
            Orleans.Serialization.WireProtocol.Field field)
        {
            if (field.IsReference)
            {
                return ReferenceCodec.ReadReference<Response<TResult>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            var result = ResponsePool.Get<TResult>();
            reader.ReadFieldHeader(ref field);
            if (!field.IsEndBaseOrEndObject)
            {
                result.TypedResult = Codec.ReadValue(ref reader, field);
                reader.ReadFieldHeader(ref field);
            }
            return result;
        }

        public override void WriteRaw<TBufferWriter>(
            ref Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
            object value)
        {
            writer.WriteStartObject(0, null, _resultType);
            var holder = (Response<TResult>)value;
            if (holder.TypedResult is not null)
            {
                Codec.WriteField(ref writer, 0, _resultType, holder.TypedResult);
            }
            writer.WriteEndObject();
        }

        public override object ReadRaw<TInput>(
            ref Orleans.Serialization.Buffers.Reader<TInput> reader,
            scoped ref Orleans.Serialization.WireProtocol.Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = ResponsePool.Get<TResult>();
            reader.ReadFieldHeader(ref field);
            if (!field.IsEndBaseOrEndObject)
            {
                result.TypedResult = Codec.ReadValue(ref reader, field);
                reader.ReadFieldHeader(ref field);
            }
            return result;
        }
    }

    internal sealed class OsResponseActivator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult> : IActivator<Response<TResult>>
    {
        public Response<TResult> Create() => ResponsePool.Get<TResult>();
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Microsoft.Extensions.Primitives.StringValues))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "Microsoft.AspNetCore.Http.CoercedAwaitableInfo", "Microsoft.AspNetCore.Http.Extensions")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "Microsoft.AspNetCore.Http.ObjectMethodExecutorAwaitable", "Microsoft.AspNetCore.Http.Extensions")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "Microsoft.AspNetCore.Http.RequestDelegateFactory", "Microsoft.AspNetCore.Http.Extensions")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Threading.Tasks.Task))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Runtime.CompilerServices.TaskAwaiter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Threading.Tasks.ValueTask))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Runtime.CompilerServices.ValueTaskAwaiter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Threading.Tasks.Task<Microsoft.AspNetCore.Http.IResult>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Runtime.CompilerServices.TaskAwaiter<Microsoft.AspNetCore.Http.IResult>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Threading.Tasks.ValueTask<Microsoft.AspNetCore.Http.IResult>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Runtime.CompilerServices.ValueTaskAwaiter<Microsoft.AspNetCore.Http.IResult>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Threading.Tasks.Task<object>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Runtime.CompilerServices.TaskAwaiter<object>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Threading.Tasks.ValueTask<object>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(System.Runtime.CompilerServices.ValueTaskAwaiter<object>))]
    private static void PreserveHttpAwaitable(Type type) => _ = type;
}
