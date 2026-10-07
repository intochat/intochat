
namespace DigitalBrain.Framework.Types;

public enum McpFormPrimitive
{
    String,
    Number,
    Boolean,
    Enum,
    MultiChoiceEnum,
}

public static class SchemaExtensions
{
    public static McpFormPrimitive? ToMcpFormPrimitive(this SemanticType type) => type.Kind switch
    {
        FieldKind.PlainText => McpFormPrimitive.String,
        FieldKind.Number => McpFormPrimitive.Number,
        FieldKind.Boolean => McpFormPrimitive.Boolean,
        FieldKind.Date => McpFormPrimitive.String,
        FieldKind.DateTime => McpFormPrimitive.String,
        FieldKind.Email => McpFormPrimitive.String,
        FieldKind.Url => McpFormPrimitive.String,
        FieldKind.Choice => McpFormPrimitive.Enum,
        FieldKind.MultiChoice => McpFormPrimitive.MultiChoiceEnum,
        _ => null,
    };
}
