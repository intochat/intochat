using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Connections;

public enum FieldKind
{
    PlainText,
    LongText,
    Number,
    Date,
    DateTime,
    Boolean,
    Choice,
    MultiChoice,
    Email,
    Url,
    Secret,
    Reference,
}
