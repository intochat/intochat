using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Agents.Tools;

internal interface IToolRouter
{
    Task<IReadOnlyList<AIFunction>> Route(string text, int k = 8, CancellationToken ct = default);
}
