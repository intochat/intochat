using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Serialization;
using DigitalBrain.Runtime.Orleans.Synapses;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

#nullable disable

namespace DigitalBrain.Runtime.Orleans.Hosting;

internal static class OrleansRuntimeAot
{
    public static void Register(IServiceCollection services)
    {
        AddEnum<BrainRecordChange>(services);
        AddEnum<BrainLogFormat>(services);
        AddEnum<JournalStateChange>(services);
        AddEnum<HostPolicy>(services);
        AddEnum<CapabilityConnectionMode>(services);
        AddEnum<ClusteringBinding>(services);
        AddEnum<GrainStoreBinding>(services);
        AddEnum<RemindersBinding>(services);
        AddEnum<SecretsBinding>(services);
        AddEnum<HttpBinding>(services);
        AddEnum<SandboxBinding>(services);
        AddEnum<ModelBinding>(services);
        AddEnum<UiBinding>(services);
        AddEnum<ClusterStoreBinding>(services);
        AddEnum<Topology>(services);
        AddEnum<DigitalBrain.Confirmation.ConfirmationAction>(services);
        AddEnum<DigitalBrain.Confirmation.ConfirmationPolicy>(services);
        AddEnum<DigitalBrain.Confirmation.ConfirmationOutcome>(services);
        AddEnum<DigitalBrain.Confirmation.ConfirmationDenyReason>(services);
        AddEnum<DigitalBrain.Confirmation.PrincipalKind>(services);
        AddEnum<DigitalBrain.Flows.StepOutcome>(services);
        AddEnum<DigitalBrain.Flows.WaitOutcome>(services);
        AddEnum<DigitalBrain.Flows.FlowOutcome>(services);
        AddEnum<DigitalBrain.Flows.FlowStatus>(services);
        AddEnum<DigitalBrain.Flows.WaitReason>(services);
        AddEnum<DigitalBrain.Flows.RelationKind>(services);
        AddEnum<DigitalBrain.Flows.StepDisposition>(services);
        AddEnum<InvocationResultKind>(services);
        AddEnum<DigitalBrain.Modules.NeedKind>(services);
        AddEnum<DeactivationCause>(services);
        AddEnum<DigitalBrain.Operations.OperationPhase>(services);
        AddEnum<DigitalBrain.Limits.ProposalStatus>(services);
        AddEnum<DigitalBrain.Limits.PolicyMeasure>(services);
        AddEnum<DigitalBrain.Limits.PolicyWindowKind>(services);
        AddEnum<DigitalBrain.Limits.PolicyOp>(services);
        AddEnum<DigitalBrain.Units.SynapseKind>(services);
        AddEnum<AnswerStatus>(services);
        AddEnum<DigitalBrain.Units.DeployStatus>(services);
        AddEnum<DigitalBrain.Units.InstallStatus>(services);
        AddEnum<DigitalBrain.Time.ScheduleKind>(services);
        AddEnum<DigitalBrain.Units.UnitView>(services);
        AddEnum<TraceDetailStatus>(services);
        AddEnum<CostKind>(services);
        AddEnum<UsageGrouping>(services);
        AddEnum<ExternalCallOutcomeKind>(services);
        AddEnum<ExternalAccountingStatus>(services);
        AddEnum<ExternalCallState>(services);
        AddEnum<FundingKind>(services);
        AddEnum<PricingStatus>(services);
        AddEnum<ExternalChargePolicy>(services);
        AddEnum<DigitalBrain.Capabilities.Connections.Connectors.OAuthTokenFailure>(services);
        AddEnum<IdentityPosture>(services);
        AddEnum<DigitalBrain.Capabilities.Identity.Model.Refusal>(services);
        AddEnum<SecretVaultStatus>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.ItemKind>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.ItemFlags>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.SortKey>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.SearchMode>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.QuotaState>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.WriteKind>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.ConflictPolicy>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.Confinement.ConfinementReason>(services);
        AddEnum<DigitalBrain.Capabilities.Storage.Confinement.PathFlavor>(services);
        AddEnum<DigitalBrain.UI.FieldKind>(services);
        AddEnum<DigitalBrain.UI.IntentKind>(services);
        AddEnum<DigitalBrain.UI.BadgeTone>(services);
        AddEnum<ConnectionStatus>(services);
        AddEnum<ConnectionProbeOutcome>(services);
        AddEnum<IntegrationStatus>(services);
        AddEnum<SecretStatus>(services);
        AddEnum<MemberRole>(services);
        AddEnum<BrainKind>(services);
        AddEnum<CallDenial>(services);
        AddEnum<GrantMode>(services);
        AddEnum<TypeKind>(services);
        AddEnum<TypeKindSurrogate>(services);
        AddEnum<SiloStatus>(services);
        AddEnum<EnumerationResult>(services);

        AddValue<DateTimeOffset>(services);
        AddNullableValue<DateTimeOffset>(services);
        AddValue<TimeSpan>(services);
        AddNullableValue<TimeSpan>(services);
        AddValue<long>(services);
        AddNullableValue<long>(services);
        AddValue<int>(services);
        AddNullableValue<int>(services);
        AddValue<double>(services);
        AddNullableValue<double>(services);
        AddValue<bool>(services);
        AddNullableValue<bool>(services);
        AddValue<Guid>(services);
        AddNullableValue<Guid>(services);
        AddValue<byte>(services);
        AddNullableValue<byte>(services);
        AddValue<float>(services);
        AddNullableValue<float>(services);
        AddValue<decimal>(services);
        AddNullableValue<decimal>(services);

        AddValueSurrogate<JsonElement, JsonElementSurrogate, JsonElementSurrogateConverter>(services);
        AddClassSurrogate<RuntimeInvocation, RuntimeInvocationSurrogate, RuntimeInvocationSurrogateConverter>(services);
        AddClassSurrogate<BrainInstance, BrainInstanceSurrogate, BrainInstanceSurrogateConverter>(services);
        AddValueSurrogate<TypeKind, TypeKindSurrogate, TypeKindSurrogateConverter>(services);
        AddClassSurrogate<BrainType, BrainTypeSurrogate, BrainTypeSurrogateConverter>(services);
        AddClassSurrogate<ConnectionView, ConnectionViewSurrogate, ConnectionViewSurrogateConverter>(services);
        AddClassSurrogate<OAuthGrant, OAuthGrantSurrogate, OAuthGrantSurrogateConverter>(services);
        AddClassSurrogate<ConnectionProbeResult, ConnectionProbeResultSurrogate, ConnectionProbeResultSurrogateConverter>(services);
        AddClassSurrogate<IntegrationSnapshot, IntegrationSnapshotSurrogate, IntegrationSnapshotSurrogateConverter>(services);
        AddClassSurrogate<ConfigureIntegration, ConfigureIntegrationSurrogate, ConfigureIntegrationSurrogateConverter>(services);
        AddClassSurrogate<ReleasedIntegration, ReleasedIntegrationSurrogate, ReleasedIntegrationSurrogateConverter>(services);
        AddClassSurrogate<Trace, TraceSurrogate, TraceSurrogateConverter>(services);
        AddClassSurrogate<Owner, OwnerSurrogate, OwnerSurrogateConverter>(services);
        AddClassSurrogate<BrainResidency, BrainResidencySurrogate, BrainResidencySurrogateConverter>(services);
        AddClassSurrogate<BrainResidency.Ephemeral, BrainResidencySurrogate, EphemeralBrainResidencySurrogateConverter>(services);
        AddClassSurrogate<BrainResidency.Persistent, BrainResidencySurrogate, PersistentBrainResidencySurrogateConverter>(services);
        AddClassSurrogate<BrainState, BrainStateSurrogate, BrainStateSurrogateConverter>(services);
        AddClassSurrogate<SealedSecret, SealedSecretSurrogate, SealedSecretSurrogateConverter>(services);
        AddClassSurrogate<SecretRef, SecretRefSurrogate, SecretRefSurrogateConverter>(services);
        AddClassSurrogate<Account, AccountSurrogate, AccountSurrogateConverter>(services);
        AddClassSurrogate<Member, MemberSurrogate, MemberSurrogateConverter>(services);
        AddClassSurrogate<DigitalBrain.Capabilities.Identity.BrainEntry, BrainEntrySurrogate, BrainEntrySurrogateConverter>(services);
        AddClassSurrogate<BrainProvision, BrainProvisionSurrogate, BrainProvisionSurrogateConverter>(services);
        AddClassSurrogate<CallDecision, CallDecisionSurrogate, CallDecisionSurrogateConverter>(services);
        AddClassSurrogate<Grant, GrantSurrogate, GrantSurrogateConverter>(services);
        AddValueSurrogate<NeuronId, NeuronIdSurrogate, NeuronIdSurrogateConverter>(services);

        Add<bool>(services);
        Add<int>(services);
        Add<long>(services);
        Add<uint>(services);
        Add<ulong>(services);
        Add<double>(services);
        Add<string>(services);
        Add<string[]>(services);
        Add<object>(services);
        Add<object[]>(services);
        Add<byte[]>(services);
        Add<Guid>(services);
        Add<DateTimeOffset>(services);
        Add<TimeSpan>(services);
        Add<JsonElement>(services);
        Add<Nullable<JsonElement>>(services);

        Add<MembershipTableData>(services);
        Add<MembershipEntry[]>(services);
        Add<TableVersion>(services);
        Add<SiloAddress>(services);
        Add<List<GrainId>>(services);
        Add<List<DetailedGrainStatistic>>(services);
        Add<DetailedGrainStatistic[]>(services);
        Add<List<Tuple<GrainId, string, int>>>(services);
        Add<SiloRuntimeStatistics>(services);
        Add<SiloRuntimeStatistics[]>(services);
        Add<SimpleGrainStatistic[]>(services);
        Add<ClusterManifest>(services);
        Add<GrainManifest>(services);
        Add<Dictionary<SiloAddress, SiloStatus>>(services);
        Add<List<GrainCallFrequency>>(services);
        Add<IndirectProbeResponse>(services);
        Add<global::Orleans.Concurrency.Immutable<List<GrainAddress>>>(services);
        Add<global::Orleans.Concurrency.Immutable<List<GrainId>>>(services);
        Add<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>>(services);
        Add<(EnumerationResult, object)>(services);
        Add<Dictionary<GrainInterfaceType, CompatibilityStrategy>>(services);
        Add<CompatibilityStrategy>(services);
        Add<Dictionary<GrainInterfaceType, VersionSelectorStrategy>>(services);
        Add<VersionSelectorStrategy>(services);

        Add<IGrainReminder>(services);
        Add<List<IGrainReminder>>(services);
        Add<ReminderEntry>(services);
        Add<ReminderTableData>(services);

        Add<Member>(services);
        Add<IntegrationSnapshot>(services);
        Add<ReleasedIntegration>(services);

        services.Configure<TypeManifestOptions>(options =>
        {
            options.Copiers.RemoveWhere(static t => t.Name.StartsWith("PooledResponseCopier", StringComparison.Ordinal));
            options.Serializers.RemoveWhere(static t => t.Name.StartsWith("PooledResponseCodec", StringComparison.Ordinal));
            options.Activators.RemoveWhere(static t => t.Name.StartsWith("PooledResponseActivator", StringComparison.Ordinal));
            options.Copiers.RemoveWhere(static t => t.Name.StartsWith("SurrogateCopier", StringComparison.Ordinal));

            options.Copiers.Add(typeof(ClosedResponseCopier<>));
            options.Serializers.Add(typeof(ClosedResponseCodec<>));
            options.Activators.Add(typeof(ClosedResponseActivator<>));
            options.Copiers.Add(typeof(ClosedSurrogateCopier<,,>));
        });
    }

    internal static void Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult>(IServiceCollection services)
    {
        _ = ResponsePool.Get<TResult>();
        services.AddSingleton<IDeepCopier<TResult>>(static sp => sp.GetRequiredService<ICodecProvider>().GetDeepCopier<TResult>());
        services.AddSingleton<IFieldCodec<TResult>>(static sp => sp.GetRequiredService<ICodecProvider>().GetCodec<TResult>());
        services.AddSingleton<IDeepCopier<Response<TResult>>, ClosedResponseCopier<TResult>>();
        services.AddSingleton<IFieldCodec<Response<TResult>>, ClosedResponseCodec<TResult>>();
        services.AddSingleton<IActivator<Response<TResult>>, ClosedResponseActivator<TResult>>();
        services.AddSingleton<ClosedResponseCopier<TResult>>();
        services.AddSingleton<ClosedResponseCodec<TResult>>();
        services.AddSingleton<ClosedResponseActivator<TResult>>();
    }

    internal static void AddEnum<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TEnum>(IServiceCollection services)
        where TEnum : struct, Enum
    {
        services.AddSingleton<IFieldCodec<TEnum>, ClosedEnumCodec<TEnum>>();
        services.AddSingleton<ShallowCopier<TEnum>>();
        services.AddSingleton<IDeepCopier<TEnum>>(static sp => sp.GetRequiredService<ShallowCopier<TEnum>>());
        services.AddSingleton<IActivator<TEnum>, ClosedStructActivator<TEnum>>();
        AddNullableValue<TEnum>(services);
    }

    internal static void AddValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>(IServiceCollection services)
        where TValue : struct
    {
        services.AddSingleton<IFieldCodec<TValue>>(static sp => sp.GetRequiredService<ICodecProvider>().GetCodec<TValue>());
        services.AddSingleton<IDeepCopier<TValue>>(static sp => sp.GetRequiredService<ICodecProvider>().GetDeepCopier<TValue>());
        services.AddSingleton<IActivator<TValue>, ClosedStructActivator<TValue>>();
    }

    internal static void AddNullableValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TValue>(IServiceCollection services)
        where TValue : struct
    {
        services.AddSingleton<IFieldCodec<TValue?>>(static sp => new NullableCodec<TValue>(sp.GetRequiredService<IFieldCodec<TValue>>()));
        services.AddSingleton<IDeepCopier<TValue?>>(static sp => new NullableCopier<TValue>(sp.GetRequiredService<IDeepCopier<TValue>>()));
        services.AddSingleton<IActivator<TValue?>, ClosedStructActivator<TValue?>>();
    }

    internal static void AddClassSurrogate<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TField,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSurrogate,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TConverter>(IServiceCollection services)
        where TField : class
        where TSurrogate : struct
        where TConverter : class, IConverter<TField, TSurrogate>, new()
    {
        services.AddSingleton<TConverter>();
        services.AddSingleton<IConverter<TField, TSurrogate>>(static sp => sp.GetRequiredService<TConverter>());
        services.AddSingleton<IDeepCopier<TSurrogate>>(static sp => sp.GetRequiredService<ICodecProvider>().GetDeepCopier<TSurrogate>());
        services.AddSingleton<IFieldCodec<TSurrogate>>(static sp => sp.GetRequiredService<ICodecProvider>().GetCodec<TSurrogate>());
        services.AddSingleton<IValueSerializer<TSurrogate>>(static sp => sp.GetRequiredService<ICodecProvider>().GetValueSerializer<TSurrogate>());
        services.AddSingleton<SurrogateCodec<TField, TSurrogate, TConverter>>();
        services.AddSingleton<IFieldCodec<TField>>(static sp => sp.GetRequiredService<SurrogateCodec<TField, TSurrogate, TConverter>>());
        services.AddSingleton<IBaseCodec<TField>>(static sp => sp.GetRequiredService<SurrogateCodec<TField, TSurrogate, TConverter>>());
        services.AddSingleton<ClosedSurrogateCopier<TField, TSurrogate, TConverter>>();
        services.AddSingleton<IDeepCopier<TField>>(static sp => sp.GetRequiredService<ClosedSurrogateCopier<TField, TSurrogate, TConverter>>());
    }

    internal static void AddValueSurrogate<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TField,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSurrogate,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TConverter>(IServiceCollection services)
        where TField : struct
        where TSurrogate : struct
        where TConverter : class, IConverter<TField, TSurrogate>, new()
    {
        services.AddSingleton<TConverter>();
        services.AddSingleton<IConverter<TField, TSurrogate>>(static sp => sp.GetRequiredService<TConverter>());
        services.AddSingleton<IDeepCopier<TSurrogate>>(static sp => sp.GetRequiredService<ICodecProvider>().GetDeepCopier<TSurrogate>());
        services.AddSingleton<IFieldCodec<TSurrogate>>(static sp => sp.GetRequiredService<ICodecProvider>().GetCodec<TSurrogate>());
        services.AddSingleton<IValueSerializer<TSurrogate>>(static sp => sp.GetRequiredService<ICodecProvider>().GetValueSerializer<TSurrogate>());
        services.AddSingleton<ValueTypeSurrogateCodec<TField, TSurrogate, TConverter>>();
        services.AddSingleton<IFieldCodec<TField>>(static sp => sp.GetRequiredService<ValueTypeSurrogateCodec<TField, TSurrogate, TConverter>>());
        services.AddSingleton<IValueSerializer<TField>>(static sp => sp.GetRequiredService<ValueTypeSurrogateCodec<TField, TSurrogate, TConverter>>());
        services.AddSingleton<ClosedSurrogateCopier<TField, TSurrogate, TConverter>>();
        services.AddSingleton<IDeepCopier<TField>>(static sp => sp.GetRequiredService<ClosedSurrogateCopier<TField, TSurrogate, TConverter>>());
        AddNullableValue<TField>(services);
    }

    internal sealed class ClosedStructActivator<T> : IActivator<T>
    {
        public T Create() => default;
    }

    internal sealed class ClosedEnumCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TEnum> : IFieldCodec<TEnum>
        where TEnum : struct, Enum
    {
        private readonly Type _codecFieldType = typeof(TEnum);

        public void WriteField<TBufferWriter>(
            ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
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
            ref global::Orleans.Serialization.Buffers.Reader<TInput> reader,
            global::Orleans.Serialization.WireProtocol.Field field)
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

    [RegisterCopier]
    internal sealed class ClosedSurrogateCopier<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TField,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSurrogate,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TConverter> : IDeepCopier<TField>
        where TSurrogate : struct
        where TConverter : IConverter<TField, TSurrogate>
    {
        private readonly IDeepCopier<TSurrogate> _surrogateCopier;
        private readonly TConverter _converter;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ClosedSurrogateCopier<,,>))]
        public ClosedSurrogateCopier(IDeepCopier<TSurrogate> surrogateCopier, TConverter converter)
        {
            _surrogateCopier = surrogateCopier;
            _converter = converter;
        }

        public TField DeepCopy(TField input, CopyContext context)
        {
            if (!typeof(TField).IsValueType)
            {
                if (input is null)
                {
                    return default!;
                }

                if (context.TryGetCopy<object>(input, out var existing))
                {
                    return (TField)existing;
                }
            }

            var surrogate = _converter.ConvertToSurrogate(in input);
            var copySurrogate = _surrogateCopier.DeepCopy(surrogate, context);
            var result = _converter.ConvertFromSurrogate(in copySurrogate);
            if (!typeof(TField).IsValueType && input is not null && result is not null)
            {
                context.RecordCopy(input, result);
            }

            return result;
        }
    }

    [RegisterCopier]
    internal sealed class ClosedResponseCopier<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult> : IDeepCopier<Response<TResult>>
    {
        private readonly ICodecProvider _codecProvider;
        private IDeepCopier<TResult> _copier;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ClosedResponseCopier<>))]
        public ClosedResponseCopier(ICodecProvider codecProvider)
        {
            _codecProvider = codecProvider;
        }

        public Response<TResult> DeepCopy(Response<TResult> input, CopyContext context)
        {
            if (input is null) { return null; }
            var result = ResponsePool.Get<TResult>();
            var copier = _copier ??= _codecProvider.GetDeepCopier<TResult>();
            result.TypedResult = copier.DeepCopy(input.TypedResult, context);
            return result;
        }
    }

    [RegisterSerializer]
    internal sealed class ClosedResponseCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult> : ResponseCodec, IFieldCodec<Response<TResult>>
    {
        private readonly ICodecProvider _codecProvider;
        private readonly Type _codecFieldType = typeof(Response<TResult>);
        private readonly Type _resultType = typeof(TResult);
        private IFieldCodec<TResult> _codec;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ClosedResponseCodec<>))]
        public ClosedResponseCodec(ICodecProvider codecProvider)
        {
            _codecProvider = codecProvider;
        }

        private IFieldCodec<TResult> Codec => _codec ??= _codecProvider.GetCodec<TResult>();

        public void WriteField<TBufferWriter>(
            ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
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
            ref global::Orleans.Serialization.Buffers.Reader<TInput> reader,
            global::Orleans.Serialization.WireProtocol.Field field)
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
            ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer,
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
            ref global::Orleans.Serialization.Buffers.Reader<TInput> reader,
            scoped ref global::Orleans.Serialization.WireProtocol.Field field)
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

    [RegisterActivator]
    internal sealed class ClosedResponseActivator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TResult> : IActivator<Response<TResult>>
    {
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ClosedResponseActivator<>))]
        public ClosedResponseActivator()
        {
        }

        public Response<TResult> Create() => ResponsePool.Get<TResult>();
    }
}
