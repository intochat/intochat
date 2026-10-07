using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp;

internal sealed class CSharpAppBindingGuard : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod.DeclaringType == typeof(ICSharpFile) && context.Grain is CSharpFileNeuron file)
        {
            file.RequireBoundAccess(context.SourceId, context.InterfaceMethod.Name);
        }
        if (context.InterfaceMethod.DeclaringType == typeof(ICSharpAppBinding))
        {
            var source = context.SourceId;
            if (source is null || source.Value.Type.ToString() != DigitalBrain.Framework.AppGrainType.Name
                || source.Value.Key.ToString() != (string)context.Request.GetArgument(0)!)
            { throw new UnauthorizedAccessException("Only the originating Apps grain may bind a behavior."); }
            _ = BrainScope.CurrentId();
        }
        await context.Invoke();
    }
}
