using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections;

public enum SemanticFieldKind
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
