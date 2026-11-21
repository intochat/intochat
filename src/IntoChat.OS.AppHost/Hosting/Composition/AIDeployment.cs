using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Web;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

internal sealed record AIDeploymentPlan(
    AIOptions Ai,
    DevicesOptions? Devices,
    WebModuleOptions? Web,
    McpModuleOptions? Mcp);

public sealed class AIDeployment
{
    private DeviceDeployment? _device;
    private CloudDeployment? _cloud;
    private WebDeployment? _web;

    public AIDeployment Device(Action<DeviceDeployment> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _device ??= new DeviceDeployment();
        configure(_device);
        return this;
    }

    public AIDeployment Cloud(Action<CloudDeployment> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _cloud ??= new CloudDeployment();
        configure(_cloud);
        return this;
    }

    public AIDeployment Web(Action<WebDeployment> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _web ??= new WebDeployment();
        configure(_web);
        return this;
    }

    internal AIDeploymentPlan Resolve(DeploymentProfile profile, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configuration);

        var ai = new AIOptions();
        DevicesOptions? devices = null;

        if (profile.UsesDevice)
        {
            if (_device is not null)
            {
                foreach (var (role, card) in _device.RoleBindings.Bindings)
                {
                    ai.Roles.Device[role] = card.Id.Value;
                }

                devices = new DevicesOptions
                {
                    Ollama = new OllamaRuntimeOptions
                    {
                        Enabled = _device.OllamaEnabled,
                        Endpoint = _device.OllamaEndpoint ?? IntoChatOSNames.LocalOllamaEndpoint,
                    },
                    FoundryLocal = new FoundryLocalRuntimeOptions
                    {
                        Enabled = _device.FoundryLocalEnabled,
                    },
                };

                foreach (var (name, url) in _device.Attached)
                {
                    devices.Attach[name] = new AttachedRuntimeOptions { Url = url };
                }

                var prefetch = new List<string>();
                foreach (var card in _device.PrefetchCards)
                {
                    AddDistinct(prefetch, card.Id.Value);
                }

                if (_device.RoleBindings.Bindings.TryGetValue(Role.Hear, out var hearCard))
                {
                    AddDistinct(prefetch, hearCard.Id.Value);
                }

                if (configuration[IntoChatOSHostingOptions.LocalModelKey] is { Length: > 0 } localModel
                    && !string.IsNullOrWhiteSpace(localModel))
                {
                    var trimmed = localModel.Trim();
                    var resolved = KnownModels.Find(trimmed)?.Id.Value ?? trimmed;
                    ai.Roles.Device[Role.Chat] = resolved;
                    AddDistinct(prefetch, resolved);
                }

                devices.Prefetch = prefetch;
            }
            else if (configuration[IntoChatOSHostingOptions.LocalModelKey] is { Length: > 0 } localModel
                && !string.IsNullOrWhiteSpace(localModel))
            {
                var trimmed = localModel.Trim();
                ai.Roles.Device[Role.Chat] = KnownModels.Find(trimmed)?.Id.Value ?? trimmed;
            }
        }

        if (profile.UsesCloud && _cloud is not null)
        {
            foreach (var (role, card) in _cloud.RoleBindings.Bindings)
            {
                ai.WithRole(role, card, Where.Cloud);
            }
        }

        WebModuleOptions? web = null;
        McpModuleOptions? mcp = null;
        if (_web is not null)
        {
            var provider = _web.SearXngEnabled && profile.UsesDevice
                ? SearchProvider.Searxng
                : _web.BraveEnabled && (!_web.BraveCloudOnly || profile.Cloud)
                    ? SearchProvider.Brave
                    : SearchProvider.Off;

            web = new WebModuleOptions
            {
                Provider = provider,
                SearxngUrl = provider == SearchProvider.Searxng ? new Uri("http://localhost:8080") : null,
            };

            mcp = new McpModuleOptions();
            mcp.Servers["microsoft-learn"].Enabled = _web.MicrosoftLearnEnabled;
        }

        return new AIDeploymentPlan(ai, devices, web, mcp);
    }

    private static void AddDistinct(List<string> list, string id)
    {
        if (!list.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(id);
        }
    }
}

public sealed class DeviceDeployment
{
    private readonly Dictionary<string, string> _attached = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModelCard> _prefetchCards = [];

    internal bool OllamaEnabled { get; private set; }
    internal string? OllamaEndpoint { get; private set; }
    internal bool FoundryLocalEnabled { get; private set; }
    internal IReadOnlyDictionary<string, string> Attached => _attached;
    internal RoleBindings RoleBindings { get; } = new();
    internal IReadOnlyList<ModelCard> PrefetchCards => _prefetchCards;

    public DeviceDeployment Ollama(Uri? endpoint = null)
    {
        if (endpoint is not null)
        {
            ValidateHttpUri(endpoint, nameof(endpoint));
            OllamaEndpoint = endpoint.OriginalString;
        }

        OllamaEnabled = true;
        return this;
    }

    public DeviceDeployment FoundryLocal()
    {
        FoundryLocalEnabled = true;
        return this;
    }

    public DeviceDeployment Attach(string name, Uri url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(url);
        ValidateHttpUri(url, nameof(url));
        _attached[name.Trim()] = url.OriginalString;
        return this;
    }

    public DeviceDeployment Roles(Action<RoleBindings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(RoleBindings);
        foreach (var (role, card) in RoleBindings.Bindings)
        {
            if (card.Where != Where.Device)
            {
                throw new ArgumentException(
                    $"Model '{card.Id.Value}' cannot fill Device role {role}: it runs in {card.Where}, not Device.",
                    nameof(configure));
            }
        }
        return this;
    }

    public DeviceDeployment Prefetch(params ModelCard[] cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        foreach (var card in cards)
        {
            ArgumentNullException.ThrowIfNull(card);
            if (card.Where != Where.Device)
            {
                throw new ArgumentException(
                    $"Model '{card.Id.Value}' cannot be prefetched on a device: it runs in {card.Where}, not Device.",
                    nameof(cards));
            }
            _prefetchCards.Add(card);
        }
        return this;
    }

    private static void ValidateHttpUri(Uri uri, string paramName)
    {
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"'{uri}' must be an absolute HTTP(S) URI without credentials.", paramName);
        }
    }
}

public sealed class CloudDeployment
{
    internal RoleBindings RoleBindings { get; } = new();

    public CloudDeployment Roles(Action<RoleBindings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(RoleBindings);
        foreach (var (role, card) in RoleBindings.Bindings)
        {
            if (card.Where == Where.Device)
            {
                throw new ArgumentException(
                    $"Model '{card.Id.Value}' cannot fill Cloud role {role}: it runs on {card.Where}, not Cloud.",
                    nameof(configure));
            }
        }
        return this;
    }
}

public sealed class RoleBindings
{
    private readonly Dictionary<Role, ModelCard> _bindings = [];

    internal IReadOnlyDictionary<Role, ModelCard> Bindings => _bindings;

    public RoleBindings Chat(ModelCard card) => Bind(Role.Chat, card);
    public RoleBindings Think(ModelCard card) => Bind(Role.Think, card);
    public RoleBindings Fast(ModelCard card) => Bind(Role.Fast, card);
    public RoleBindings Hear(ModelCard card) => Bind(Role.Hear, card);
    public RoleBindings Speak(ModelCard card) => Bind(Role.Speak, card);
    public RoleBindings Talk(ModelCard card) => Bind(Role.Talk, card);
    public RoleBindings Embed(ModelCard card) => Bind(Role.Embed, card);
    public RoleBindings Image(ModelCard card) => Bind(Role.Image, card);

    private RoleBindings Bind(Role role, ModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var required = RoleNeeds.Of(role);
        if ((card.Can & required) != required)
        {
            throw new ArgumentException(
                $"Model '{card.Id.Value}' cannot fill role {role}: it requires {required}, but has {card.Can}.",
                nameof(card));
        }

        _bindings[role] = card;
        return this;
    }
}

public sealed class WebDeployment
{
    internal bool SearXngEnabled { get; private set; }
    internal bool BraveEnabled { get; private set; }
    internal bool BraveCloudOnly { get; private set; }
    internal bool MicrosoftLearnEnabled { get; private set; }

    public WebDeployment SearXng()
    {
        SearXngEnabled = true;
        return this;
    }

    public WebDeployment Brave(bool cloudOnly = false)
    {
        BraveEnabled = true;
        BraveCloudOnly = cloudOnly;
        return this;
    }

    public WebDeployment MicrosoftLearn()
    {
        MicrosoftLearnEnabled = true;
        return this;
    }
}
