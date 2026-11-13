using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.AI.Models.OpenAI;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Apps;

internal sealed class CreatorPolicyModel(IServiceProvider plain, IOptions<AppsOptions> options) : IPolicyModel
{
    private string Model => options.Value.AuthorModel ?? nameof(IGpt56Luna);

    public Task<string> Complete(string brainId, string system, string prompt, CancellationToken ct)
        => CreatorModel.Complete(plain, brainId, Model, system, prompt, ct);
}
