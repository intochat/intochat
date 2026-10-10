using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Circles;

// The kernel names the grain that made each call (SourceId); an [Internal] circle method is accepted only
// from the sibling copy the protocol expects, and the brain that copy belongs to is handed to the method
// (From), never read from an argument.
internal sealed class CircleCallGuard : IIncomingGrainCallFilter
{
    public const string From = "circles.from";
    public const string CircleType = CircleGrainType.Name;
    public const string ChatType = "circles.chat";
    public const string IndexType = "circles.index";

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        var target = context.TargetId.Type.ToString();
        if (target is not (CircleType or ChatType or IndexType) || !Attribute.IsDefined(context.InterfaceMethod, typeof(InternalAttribute)))
        { await context.Invoke(); return; }

        var from = Caller(context, target) ?? throw new UnauthorizedAccessException(
            $"{context.InterfaceMethod.Name} on {context.TargetId.Key} is not open to this caller.");
        RequestContext.Set(From, from);
        await context.Invoke();
    }

    private static string? Caller(IIncomingGrainCallContext context, string targetType)
    {
        if (context.SourceId is not { } source) { return null; }
        var sourceType = source.Type.ToString();
        var targetKey = context.TargetId.Key.ToString()!;
        var sourceKey = source.Key.ToString()!;
        var method = context.InterfaceMethod.Name;

        if (targetType == IndexType)
        {
            var brain = targetKey[..^"/circles".Length];
            var copy = sourceType == CircleType ? CircleAddress.TryParse(sourceKey)
                : sourceType == ChatType && sourceKey.EndsWith("/chat", StringComparison.Ordinal) ? CircleAddress.TryParse(sourceKey[..^"/chat".Length]) : null;
            return copy is { } held && held.Brain == brain ? brain : null;
        }

        if (targetType == CircleType)
        {
            if (sourceType == ChatType && method == nameof(ICircle.Roster))
            { return CircleAddress.TryParse(targetKey) is { } mine && sourceKey == mine.ChatKey ? mine.Brain : null; }
            if (sourceType != CircleType || CircleAddress.TryParse(targetKey) is not { } target || CircleAddress.TryParse(sourceKey) is not { } sibling
                || sibling.Name != target.Name || sibling.Owner != target.Owner) { return null; }
            return method switch
            {
                nameof(ICircle.Invited) or nameof(ICircle.Changed) => !target.IsOwnerCopy && sibling.IsOwnerCopy ? sibling.Brain : null,
                nameof(ICircle.Joined) or nameof(ICircle.Left) or nameof(ICircle.StateForCaller) => target.IsOwnerCopy && !sibling.IsOwnerCopy ? sibling.Brain : null,
                _ => null,
            };
        }

        if (sourceType != ChatType || CircleAddress.TryParse(targetKey[..Math.Max(0, targetKey.Length - "/chat".Length)]) is not { } chat
            || CircleAddress.TryParse(sourceKey[..Math.Max(0, sourceKey.Length - "/chat".Length)]) is not { } other
            || other.Name != chat.Name || other.Owner != chat.Owner) { return null; }
        return method switch
        {
            nameof(ICircleChat.Relay) or nameof(ICircleChat.Pull) => chat.IsOwnerCopy && !other.IsOwnerCopy ? other.Brain : null,
            nameof(ICircleChat.Notify) => !chat.IsOwnerCopy && other.IsOwnerCopy ? other.Brain : null,
            _ => null,
        };
    }
}
