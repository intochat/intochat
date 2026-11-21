using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Identity.Codes;

internal sealed class CodeBook : ICodeBook
{
    private static readonly BrainId SystemBrain = new("system");

    private readonly IServiceProvider _services;
    private readonly Func<IServiceProvider, ICodeKey> _key;
    private readonly Func<IServiceProvider, string, ICodeIndex> _index;

    public CodeBook(IServiceProvider services)
        : this(
            services,
            static sp => SiloNeurons.Get<ICodeKey>(sp, ICodeKey.Key),
            static (sp, hash) => SiloNeurons.Get<ICodeIndex>(sp, ICodeIndex.Key(hash)))
    {
    }

    internal CodeBook(
        IServiceProvider services,
        Func<IServiceProvider, ICodeKey> key,
        Func<IServiceProvider, string, ICodeIndex> index)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(index);
        _services = services;
        _key = key;
        _index = index;
    }

    public async Task<string> Bind(Code code, CodePurpose purpose, BrainId brain, string reference, DateTimeOffset expiresAt)
    {
        var edge = _services.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module(IdentityModule.Id, SystemBrain)))
        {
            var key = await _key(_services).Get().ConfigureAwait(false);
            var hash = code.Hash(key);
            await _index(_services, hash).Bind(purpose, brain.Value, reference, expiresAt).ConfigureAwait(false);
            return hash;
        }
    }

    public async Task<string> HashOf(Code code)
    {
        var edge = _services.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module(IdentityModule.Id, SystemBrain)))
        {
            var key = await _key(_services).Get().ConfigureAwait(false);
            return code.Hash(key);
        }
    }

    public async Task<CodeTarget?> Find(CodePurpose purpose, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var edge = _services.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module(IdentityModule.Id, SystemBrain)))
        {
            return await _index(_services, hash).Resolve(purpose).ConfigureAwait(false);
        }
    }

    public async Task<CodeTarget?> Lookup(CodePurpose purpose, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var edge = _services.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module(IdentityModule.Id, SystemBrain)))
        {
            return await _index(_services, hash).Lookup(purpose).ConfigureAwait(false);
        }
    }

    public async Task Spend(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var edge = _services.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module(IdentityModule.Id, SystemBrain)))
        {
            await _index(_services, hash).Spend().ConfigureAwait(false);
        }
    }
}
