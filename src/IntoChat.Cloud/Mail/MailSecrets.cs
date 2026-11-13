using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Mail;

internal sealed class MailSecrets(IServiceProvider services, IMailHost host)
{
    public const string AcsConnection = "acs-email";
    public const string RecipientHashing = "recipient-hashing";

    public async Task<string?> Named(string name)
    {
        var secrets = (services as IKeyedServiceProvider ?? services.GetService<IKeyedServiceProvider>())?.GetKeyedService<ISecrets>(Capability.Secrets.V1Id);
        if (secrets is null) { return null; }
        using var _ = host.StampModule();
        var reference = await secrets.Named(name);
        return reference.Status == SecretStatus.Set ? await secrets.Resolve(reference) : null;
    }
}
