using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Postgres.Tables;

/// <summary>Marks the primary-key property of a row type used with <see cref="TypedTable{TRow}"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TableKeyAttribute : Attribute;

/// <summary>Typed wrapper over <see cref="ITable"/> mapping a record or class to table columns via JSON.</summary>
public interface ITable<TRow> where TRow : class
{
    TableDefinition Definition { get; }
    Task<AcceptedTable> Define();
    Task<bool> Upsert(TRow row);
    Task<TRow?> Read(string key);
    Task<bool> Delete(string key);
    Task<IReadOnlyList<TRow>> Page(int skip = 0, int take = 50);
}

/// <summary>Extension helpers for wrapping an <see cref="ITable"/> as <see cref="ITable{TRow}"/>.</summary>
public static class TypedTableExtensions
{
    public static ITable<TRow> Typed<TRow>(this ITable table, JsonSerializerOptions? jsonOptions = null)
        where TRow : class
        => new TypedTable<TRow>(table, jsonOptions);
}

/// <summary>Typed wrapper over <see cref="ITable"/> backed by <see cref="System.Text.Json"/>.</summary>
public sealed class TypedTable<TRow> : ITable<TRow> where TRow : class
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = CreateDefaultJsonOptions();
    private static readonly ConcurrentDictionary<Type, RowSchema> Schemas = new();

    private readonly ITable _table;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly RowSchema _schema;

    public TypedTable(ITable table, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
        _schema = Schemas.GetOrAdd(typeof(TRow), static type => RowSchema.Build(type));
    }

    public TableDefinition Definition => _schema.Definition;

    public Task<AcceptedTable> Define() => _table.Define(_schema.Definition);

    public Task<bool> Upsert(TRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var keyValue = SerializeProperty(_schema.KeyProperty, row);
        var values = new TableValue[_schema.ValueProperties.Length];
        for (var i = 0; i < _schema.ValueProperties.Length; i++)
        {
            values[i] = SerializeProperty(_schema.ValueProperties[i], row);
        }
        return _table.Upsert([keyValue], values);
    }

    public async Task<TRow?> Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var row = await _table.Read([EncodeKey(key)]).ConfigureAwait(false);
        return row is null ? null : Deserialize(row);
    }

    public Task<bool> Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _table.Delete([EncodeKey(key)]);
    }

    public async Task<IReadOnlyList<TRow>> Page(int skip = 0, int take = 50)
    {
        var rows = await _table.Page(skip, take).ConfigureAwait(false);
        var result = new TRow[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            result[i] = Deserialize(rows[i]);
        }
        return result;
    }

    private TableValue EncodeKey(string key)
    {
        var json = _schema.KeyProperty.ColumnType == "number"
            ? key
            : JsonSerializer.Serialize(key, _jsonOptions);
        return new TableValue(_schema.KeyProperty.ColumnName, json);
    }

    private TableValue SerializeProperty(ColumnBinding binding, TRow row)
    {
        var raw = binding.Property.GetValue(row);
        if (raw is null)
        {
            return new TableValue(binding.ColumnName, "null");
        }
        if (raw is DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime();
            return new TableValue(binding.ColumnName, JsonSerializer.Serialize(new DateTimeOffset(utc), _jsonOptions));
        }
        var json = JsonSerializer.Serialize(raw, binding.Property.PropertyType, _jsonOptions);
        return new TableValue(binding.ColumnName, json);
    }

    private TRow Deserialize(TableValue[] row)
    {
        var obj = new JsonObject();
        foreach (var cell in row)
        {
            if (_schema.ByColumnName.TryGetValue(cell.Column, out var binding))
            {
                obj[binding.Property.Name] = JsonNode.Parse(cell.Json);
            }
        }
        return obj.Deserialize<TRow>(_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize row of type {typeof(TRow).Name}.");
    }

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record ColumnBinding(PropertyInfo Property, string ColumnName, string ColumnType);

    private sealed class RowSchema
    {
        private RowSchema(
            TableDefinition definition,
            ColumnBinding keyProperty,
            ColumnBinding[] valueProperties,
            Dictionary<string, ColumnBinding> byColumnName)
        {
            Definition = definition;
            KeyProperty = keyProperty;
            ValueProperties = valueProperties;
            ByColumnName = byColumnName;
        }

        public TableDefinition Definition { get; }
        public ColumnBinding KeyProperty { get; }
        public ColumnBinding[] ValueProperties { get; }
        public Dictionary<string, ColumnBinding> ByColumnName { get; }

        public static RowSchema Build(Type type)
        {
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                .ToArray();

            if (properties.Length == 0)
            {
                throw new InvalidOperationException($"Row type {type.Name} has no readable public properties.");
            }

            var markedKeys = properties.Where(static p => p.GetCustomAttribute<TableKeyAttribute>() is not null).ToArray();
            if (markedKeys.Length > 1)
            {
                throw new InvalidOperationException($"Row type {type.Name} has multiple [TableKey] properties; mark exactly one.");
            }

            var keyProp = markedKeys.Length == 1
                ? markedKeys[0]
                : properties.FirstOrDefault(static p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase))
                    ?? properties.FirstOrDefault(static p => string.Equals(p.Name, "Key", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Row type {type.Name} must declare a [TableKey] property (or an Id/Key property).");

            var bindings = new List<ColumnBinding>(properties.Length);
            var columns = new List<TableColumn>(properties.Length);
            var byColumn = new Dictionary<string, ColumnBinding>(StringComparer.Ordinal);
            ColumnBinding? keyBinding = null;
            var valueBindings = new List<ColumnBinding>(properties.Length - 1);

            foreach (var prop in properties)
            {
                var colName = ToSnakeCase(prop.Name);
                var colType = MapColumnType(prop.PropertyType);
                var binding = new ColumnBinding(prop, colName, colType);
                bindings.Add(binding);
                columns.Add(new TableColumn(colName, colType));
                byColumn[colName] = binding;

                if (prop == keyProp)
                {
                    keyBinding = binding;
                }
                else
                {
                    valueBindings.Add(binding);
                }
            }

            var definition = new TableDefinition([.. columns], [keyBinding!.ColumnName]);
            return new RowSchema(definition, keyBinding, [.. valueBindings], byColumn);
        }

        private static string MapColumnType(Type propertyType)
        {
            var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (type == typeof(string) || type == typeof(Guid) || type == typeof(char) || type == typeof(Uri) || type.IsEnum)
            {
                return "text";
            }
            if (type == typeof(bool))
            {
                return "boolean";
            }
            if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
            {
                return "timestamptz";
            }
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
                || type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                return "number";
            }
            return "jsonb";
        }

        private static string ToSnakeCase(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 && name[i - 1] != '_' && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    {
                        sb.Append('_');
                    }
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
