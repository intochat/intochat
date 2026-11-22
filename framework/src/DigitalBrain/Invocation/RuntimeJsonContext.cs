#pragma warning disable CA1822, CA1859, CA1869, CA2263, IL2026, IL2067, IL2070, IL2072, IL2075, IL3050

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DigitalBrain.Invocation;

/// <summary>Source-generated metadata for the runtime's own envelope types and the common primitive arguments (06 D-12 stays JSON).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RuntimeInvocation))]
[JsonSerializable(typeof(BrainEnvelope))]
[JsonSerializable(typeof(BrainPublicKeys))]
[JsonSerializable(typeof(RemoteInvocationRequest))]
[JsonSerializable(typeof(RemoteInvocationResponse))]
[JsonSerializable(typeof(Gap))]
[JsonSerializable(typeof(NeuronId))]
[JsonSerializable(typeof(DigitalBrain.Kernel.Subscription))]
[JsonSerializable(typeof(DigitalBrain.Kernel.Subscriptions))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class RuntimeJsonContext : JsonSerializerContext;

/// <summary>
/// The resolver behind <see cref="RuntimeProtocol.Json"/>: registered contexts first, then the runtime's own context, then reflection.
/// </summary>
internal sealed class RuntimeTypeInfoResolver : IJsonTypeInfoResolver
{
    private static readonly DefaultJsonTypeInfoResolver Reflection = new();
    private static IJsonTypeInfoResolver[] _registered = [];

    public void Register(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        IJsonTypeInfoResolver[] current, next;
        do
        {
            current = Volatile.Read(ref _registered);
            if (Array.IndexOf(current, resolver) >= 0) { return; }
            next = [.. current, resolver];
        }
        while (Interlocked.CompareExchange(ref _registered, next, current) != current);
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        foreach (var resolver in Volatile.Read(ref _registered))
        {
            if (resolver.GetTypeInfo(type, options) is { } info) { return info; }
        }
        if (((IJsonTypeInfoResolver)RuntimeJsonContext.Default).GetTypeInfo(type, options) is { } runtimeInfo)
        {
            return runtimeInfo;
        }
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return CreateAotFallbackTypeInfo(type, options);
        }
        return Reflection.GetTypeInfo(type, options);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, JsonConverter<object>> AotConverters = new();

    public static byte[] SerializeAotBytes(object? value, Type type)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = RuntimeProtocol.Json.Encoder, Indented = false, MaxDepth = RuntimeProtocol.Json.MaxDepth });
        WriteAotValue(writer, value, value?.GetType() ?? type, RuntimeProtocol.Json);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static void WriteAotValue(Utf8JsonWriter writer, object? value, Type type, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        var actualType = value.GetType();
        foreach (var resolver in Volatile.Read(ref _registered))
        {
            if (resolver.GetTypeInfo(actualType, options) is { } regInfo)
            {
                JsonSerializer.Serialize(writer, value, regInfo);
                return;
            }
        }
        if (((IJsonTypeInfoResolver)RuntimeJsonContext.Default).GetTypeInfo(actualType, options) is { } runtimeInfo)
        {
            JsonSerializer.Serialize(writer, value, runtimeInfo);
            return;
        }
        if (actualType.IsPrimitive || actualType == typeof(string) || actualType == typeof(decimal) || actualType == typeof(DateTime) || actualType == typeof(DateTimeOffset) || actualType == typeof(TimeSpan) || actualType == typeof(DateOnly) || actualType == typeof(TimeOnly) || actualType == typeof(Guid) || actualType == typeof(Uri) || actualType == typeof(Version) || actualType == typeof(JsonElement) || actualType == typeof(JsonDocument) || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(actualType) || actualType == typeof(byte[]))
        {
            JsonSerializer.Serialize(writer, value, actualType, options);
            return;
        }
        var conv = AotConverters.GetOrAdd(actualType, CreateUntypedConverter);
        conv.Write(writer, value, options);
    }

    public static object? ReadAotValue(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        foreach (var resolver in Volatile.Read(ref _registered))
        {
            if (resolver.GetTypeInfo(type, options) is { } regInfo)
            {
                return JsonSerializer.Deserialize(ref reader, regInfo);
            }
        }
        if (((IJsonTypeInfoResolver)RuntimeJsonContext.Default).GetTypeInfo(type, options) is { } runtimeInfo)
        {
            return JsonSerializer.Deserialize(ref reader, runtimeInfo);
        }
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid) || type == typeof(Uri) || type == typeof(Version) || type == typeof(JsonElement) || type == typeof(JsonDocument) || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type) || type == typeof(byte[]))
        {
            return JsonSerializer.Deserialize(ref reader, type, options);
        }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return ReadAotValue(ref reader, underlying, options);
        }
        var conv = AotConverters.GetOrAdd(type, CreateUntypedConverter);
        return conv.Read(ref reader, type, options);
    }

    public static object? DeserializeAotSpan(ReadOnlySpan<byte> utf8Json, Type type)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = RuntimeProtocol.Json.MaxDepth });
        reader.Read();
        return ReadAotValue(ref reader, type, RuntimeProtocol.Json);
    }

    public static object? DeserializeAotElement(JsonElement element, Type type)
    {
        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (type == typeof(JsonElement))
        {
            return element;
        }
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }
        return DeserializeAotSpan(buffer.WrittenSpan, type);
    }

    private static JsonConverter<object> CreateUntypedConverter(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return new AotNullableJsonConverter(type, underlying);
        }
        if (type.GetCustomAttribute<JsonConverterAttribute>() is { ConverterType: { } customConvType } && !customConvType.IsGenericTypeDefinition)
        {
            if (Activator.CreateInstance(customConvType) is JsonConverter customConv)
            {
                return new AotCustomConverterAdapter(customConv, type);
            }
        }
        if (type.IsEnum)
        {
            return new AotEnumJsonConverter(type);
        }
        if (type.IsArray && type.GetArrayRank() == 1 && type.GetElementType() is { } elemType)
        {
            return new AotArrayJsonConverter(type, elemType);
        }
        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            var genArgs = type.GetGenericArguments();
            if (genArgs.Length == 1 && (genDef == typeof(List<>) || genDef == typeof(IReadOnlyList<>) || genDef == typeof(IList<>) || genDef == typeof(ICollection<>) || genDef == typeof(IEnumerable<>) || genDef == typeof(System.Collections.Immutable.ImmutableArray<>) || genDef == typeof(System.Collections.Immutable.ImmutableList<>) || genDef == typeof(HashSet<>) || genDef == typeof(ISet<>) || genDef == typeof(IReadOnlySet<>) || genDef == typeof(System.Collections.Immutable.ImmutableHashSet<>) || genDef == typeof(System.Collections.Immutable.ImmutableSortedSet<>)))
            {
                return new AotEnumerableJsonConverter(type, genDef, genArgs[0]);
            }
            if (genArgs.Length == 2 && (genDef == typeof(Dictionary<,>) || genDef == typeof(IReadOnlyDictionary<,>) || genDef == typeof(IDictionary<,>) || genDef == typeof(System.Collections.Immutable.ImmutableDictionary<,>) || genDef == typeof(System.Collections.Immutable.ImmutableSortedDictionary<,>)) && genArgs[0] == typeof(string))
            {
                return new AotStringDictionaryJsonConverter(type, genDef, genArgs[1]);
            }
        }
        return new AotObjectJsonConverter(type);
    }

    private sealed class AotCustomConverterAdapter(JsonConverter inner, Type targetType) : JsonConverter<object>
    {
        private readonly MethodInfo? _writeMethod = inner.GetType().GetMethod("Write", [typeof(Utf8JsonWriter), targetType, typeof(JsonSerializerOptions)]);
        private readonly ConstructorInfo? _stringCtor = targetType.GetConstructor([typeof(string)]);
        private readonly MethodInfo? _parseMethod = targetType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);

        public override bool CanConvert(Type typeToConvert) => typeToConvert == targetType || inner.CanConvert(typeToConvert);

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString()!;
                if (_parseMethod is not null)
                {
                    return _parseMethod.Invoke(null, [str]);
                }
                if (_stringCtor is not null)
                {
                    return _stringCtor.Invoke([str]);
                }
            }
            return new AotObjectJsonConverter(targetType).Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (_writeMethod is not null)
            {
                _writeMethod.Invoke(inner, [writer, value, options]);
                return;
            }
            new AotObjectJsonConverter(targetType).Write(writer, value, options);
        }
    }

    private static JsonTypeInfo? CreateAotFallbackTypeInfo(Type type, JsonSerializerOptions options)
    {
        if (typeof(Task).IsAssignableFrom(type))
        {
            return null;
        }

        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid) || type == typeof(Uri) || type == typeof(Version) || type == typeof(JsonElement) || type == typeof(JsonDocument) || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type) || type == typeof(byte[]))
        {
            return Reflection.GetTypeInfo(type, options);
        }

        if (type.GetCustomAttribute<JsonConverterAttribute>() is { ConverterType: { } customConvType } && !customConvType.IsGenericTypeDefinition)
        {
            return Reflection.GetTypeInfo(type, options);
        }

        try
        {
            return Reflection.GetTypeInfo(type, options);
        }
        catch (NotSupportedException)
        {
            return JsonTypeInfo.CreateJsonTypeInfo(type, options);
        }
    }

    private static JsonTypeInfo CreateUntypedValueInfo(Type type, JsonSerializerOptions options, JsonConverter converter)
        => JsonMetadataServices.CreateValueInfo<object>(options, converter);

    private sealed class AotNullableJsonTypeInfo(Type nullableType, Type underlyingType, JsonSerializerOptions options)
    {
        public JsonTypeInfo Build()
            => CreateUntypedValueInfo(nullableType, options, new AotNullableJsonConverter(nullableType, underlyingType));
    }

    private sealed class AotNullableJsonConverter(Type nullableType, Type underlyingType) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == nullableType || typeToConvert == underlyingType;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }
            return ReadAotValue(ref reader, underlyingType, options);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }
            WriteAotValue(writer, value, underlyingType, options);
        }
    }

    private sealed class AotObjectJsonConverter : JsonConverter<object>
    {
        private readonly Type _objectType;
        private readonly ConstructorInfo? _defaultCtor;
        private readonly ConstructorInfo? _paramCtor;
        private readonly ParameterInfo[] _ctorParams;
        private readonly PropertyEntry[] _properties;
        private readonly string? _discriminatorPropertyName;
        private readonly string? _discriminatorValue;
        private readonly Dictionary<string, Type>? _derivedByDiscriminator;

        public sealed record PropertyEntry(
            PropertyInfo Property,
            string JsonName,
            FieldInfo? BackingField,
            int CtorParamIndex,
            bool IgnoreWhenNull);

        public PropertyEntry[] Entries => _properties;

        public object CreateDefault()
        {
            if (_defaultCtor is not null)
            {
                return _defaultCtor.Invoke(null);
            }
            if (_objectType.IsValueType)
            {
                return Activator.CreateInstance(_objectType)!;
            }
            if (_paramCtor is not null && _ctorParams.Length > 0)
            {
                var args = new object?[_ctorParams.Length];
                for (var i = 0; i < _ctorParams.Length; i++)
                {
                    var p = _ctorParams[i];
                    if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null)
                    {
                        args[i] = Activator.CreateInstance(p.ParameterType);
                    }
                }
                return _paramCtor.Invoke(args);
            }
            return RuntimeHelpers.GetUninitializedObject(_objectType);
        }

        public AotObjectJsonConverter(Type objectType)
        {
            _objectType = objectType;
            if (!objectType.IsAbstract && !objectType.IsInterface)
            {
                _defaultCtor = objectType.GetConstructor(Type.EmptyTypes);
                if (_defaultCtor is null || objectType.IsValueType)
                {
                    _paramCtor = objectType
                        .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                        .OrderByDescending(static c => c.GetParameters().Length)
                        .FirstOrDefault();
                }
            }
            _ctorParams = _paramCtor?.GetParameters() ?? [];

            for (var cur = objectType; cur is not null && cur != typeof(object); cur = cur.BaseType)
            {
                var poly = cur.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: false);
                var derivedAttrs = cur.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).ToArray();
                if (poly is not null || derivedAttrs.Length > 0)
                {
                    var discProp = poly?.TypeDiscriminatorPropertyName ?? "$type";
                    if (cur == objectType)
                    {
                        _discriminatorPropertyName = discProp;
                        _derivedByDiscriminator = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
                        foreach (var d in derivedAttrs)
                        {
                            if (d.TypeDiscriminator is not null)
                            {
                                _derivedByDiscriminator[d.TypeDiscriminator.ToString()!] = d.DerivedType;
                            }
                        }
                    }
                    foreach (var d in derivedAttrs)
                    {
                        if (d.DerivedType == objectType && d.TypeDiscriminator is not null)
                        {
                            _discriminatorPropertyName = discProp;
                            _discriminatorValue = d.TypeDiscriminator.ToString();
                            break;
                        }
                    }
                    break;
                }
            }

            var list = new List<PropertyEntry>();
            foreach (var prop in objectType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0 || prop.GetMethod is not { IsPublic: true })
                {
                    continue;
                }
                var ignoreAttr = prop.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true);
                if (ignoreAttr is { Condition: JsonIgnoreCondition.Always })
                {
                    continue;
                }
                var ignoreWhenNull = ignoreAttr is { Condition: JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault };
                var customName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                var jsonName = customName ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
                var backing = objectType.GetField($"<{prop.Name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                var ctorIdx = -1;
                for (var i = 0; i < _ctorParams.Length; i++)
                {
                    if (string.Equals(_ctorParams[i].Name, prop.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        ctorIdx = i;
                        break;
                    }
                }
                list.Add(new PropertyEntry(prop, jsonName, backing, ctorIdx, ignoreWhenNull));
            }
            _properties = [.. list];
        }

        public override bool CanConvert(Type typeToConvert) => typeToConvert == _objectType;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }
            if (reader.TokenType != JsonTokenType.StartObject) { throw new JsonException($"Expected StartObject for {_objectType.FullName}, got {reader.TokenType}."); }

            if (_derivedByDiscriminator is { Count: > 0 } && _discriminatorPropertyName is not null)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;
                if (root.TryGetProperty(_discriminatorPropertyName, out var discEl))
                {
                    var discStr = discEl.ValueKind == JsonValueKind.String ? discEl.GetString() : discEl.GetRawText();
                    if (discStr is not null && _derivedByDiscriminator.TryGetValue(discStr, out var derivedType) && derivedType != _objectType)
                    {
                        return DeserializeAotElement(root, derivedType);
                    }
                }
                if (_objectType.IsAbstract || _objectType.IsInterface)
                {
                    throw new JsonException($"Cannot deserialize polymorphic type '{_objectType.FullName}' without a matching '{_discriminatorPropertyName}' discriminator.");
                }
                var cloneReader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(root.GetRawText()));
                cloneReader.Read();
                return ReadConcreteObject(ref cloneReader, options);
            }

            return ReadConcreteObject(ref reader, options);
        }

        private object? ReadConcreteObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            var values = new object?[_properties.Length];
            var hasValue = new bool[_properties.Length];

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) { continue; }
                var propName = reader.GetString()!;
                reader.Read();

                var matched = -1;
                for (var i = 0; i < _properties.Length; i++)
                {
                    if (string.Equals(_properties[i].JsonName, propName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(_properties[i].Property.Name, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = i;
                        break;
                    }
                }

                if (matched >= 0)
                {
                    values[matched] = ReadAotValue(ref reader, _properties[matched].Property.PropertyType, options);
                    hasValue[matched] = true;
                }
                else
                {
                    reader.Skip();
                }
            }

            object instance;
            if (_paramCtor is not null && _ctorParams.Length > 0)
            {
                var args = new object?[_ctorParams.Length];
                for (var i = 0; i < _ctorParams.Length; i++)
                {
                    var p = _ctorParams[i];
                    if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                    else if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) is null)
                    {
                        args[i] = Activator.CreateInstance(p.ParameterType);
                    }
                }
                for (var i = 0; i < _properties.Length; i++)
                {
                    var cIdx = _properties[i].CtorParamIndex;
                    if (cIdx >= 0 && hasValue[i])
                    {
                        args[cIdx] = values[i];
                    }
                }
                instance = _paramCtor.Invoke(args);
            }
            else if (_defaultCtor is not null)
            {
                instance = _defaultCtor.Invoke(null);
            }
            else if (_objectType.IsValueType)
            {
                instance = Activator.CreateInstance(_objectType)!;
            }
            else
            {
                instance = RuntimeHelpers.GetUninitializedObject(_objectType);
            }

            for (var i = 0; i < _properties.Length; i++)
            {
                if (!hasValue[i]) { continue; }
                if (_paramCtor is not null && _ctorParams.Length > 0 && _properties[i].CtorParamIndex >= 0)
                {
                    continue;
                }
                var entry = _properties[i];
                if (entry.Property.SetMethod is not null)
                {
                    entry.Property.SetValue(instance, values[i]);
                }
                else if (entry.BackingField is not null)
                {
                    entry.BackingField.SetValue(instance, values[i]);
                }
            }

            return instance;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }
            writer.WriteStartObject();
            if (_discriminatorPropertyName is not null && _discriminatorValue is not null)
            {
                writer.WriteString(_discriminatorPropertyName, _discriminatorValue);
            }
            foreach (var entry in _properties)
            {
                var propVal = entry.Property.GetValue(value);
                if (propVal is null)
                {
                    if (entry.IgnoreWhenNull || options.DefaultIgnoreCondition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault)
                    {
                        continue;
                    }
                    writer.WritePropertyName(entry.JsonName);
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WritePropertyName(entry.JsonName);
                    WriteAotValue(writer, propVal, entry.Property.PropertyType, options);
                }
            }
            writer.WriteEndObject();
        }
    }

    private sealed class AotEnumJsonConverter(Type enumType) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == enumType;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString()!;
                return Enum.Parse(enumType, str, ignoreCase: true);
            }
            var intVal = reader.GetInt64();
            return Enum.ToObject(enumType, intVal);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class AotArrayJsonConverter(Type arrayType, Type elementType) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == arrayType;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }
            if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException($"Expected StartArray for {arrayType.FullName} (element {elementType.FullName}), got {reader.TokenType}."); }
            var items = new List<object?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                items.Add(ReadAotValue(ref reader, elementType, options));
            }
            var arr = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                arr.SetValue(items[i], i);
            }
            return arr;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in (Array)value)
            {
                WriteAotValue(writer, item, elementType, options);
            }
            writer.WriteEndArray();
        }
    }

    private sealed class AotEnumerableJsonConverter(Type collectionType, Type genericDef, Type elementType) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == collectionType;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }
            if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException($"Expected StartArray for {collectionType.FullName} (element {elementType.FullName}), got {reader.TokenType}."); }
            var items = new List<object?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                items.Add(ReadAotValue(ref reader, elementType, options));
            }
            var arr = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                arr.SetValue(items[i], i);
            }
            if (genericDef == typeof(IReadOnlyList<>) || genericDef == typeof(IList<>) || genericDef == typeof(ICollection<>) || genericDef == typeof(IEnumerable<>))
            {
                return arr;
            }
            if (collectionType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elementType)]) is { } ctor)
            {
                return ctor.Invoke([arr]);
            }
            return arr;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in (System.Collections.IEnumerable)value)
            {
                WriteAotValue(writer, item, elementType, options);
            }
            writer.WriteEndArray();
        }
    }

    private sealed class AotStringDictionaryJsonConverter(Type dictType, Type genericDef, Type valueType) : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == dictType;

        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return null; }
            if (reader.TokenType != JsonTokenType.StartObject) { throw new JsonException(); }
            var instance = Activator.CreateInstance(genericDef == typeof(IReadOnlyDictionary<,>) || genericDef == typeof(IDictionary<,>) ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType) : dictType) as System.Collections.IDictionary;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString()!;
                reader.Read();
                var val = ReadAotValue(ref reader, valueType, options);
                instance?[key] = val;
            }
            return instance;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)value)
            {
                writer.WritePropertyName((string)entry.Key);
                WriteAotValue(writer, entry.Value, valueType, options);
            }
            writer.WriteEndObject();
        }
    }
}
