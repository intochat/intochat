using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Framework.Configuration;

public static class ModuleOptionsSerialization
{
    public const string OptionsKeySuffix = ":Options";

    // Options are ordinary configuration: every property flattens to a key under
    // DigitalBrain:Modules:{Name}:Options, so a host's own configuration (env, args, files)
    // overrides a code-declared default by standard precedence — no side channel.
    public static ModuleDefinition Compile<TModule, TOptions>(TOptions options)
        where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var module = new TModule();
        return new ModuleDefinition(module, FlattenOptions(options, module.Id.ToString()));
    }

    public static Dictionary<string, string?> FlattenOptions<TOptions>(TOptions options, string moduleName)
        where TOptions : class
        => IntoChatOSConfiguration.Flatten(options!, OptionsKey(moduleName));

    public static TOptions GetModuleOptions<TOptions>(this IConfiguration configuration, string moduleName)
        where TOptions : class, IModuleOptions, new()
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(OptionsKey(moduleName)).Get<TOptions>() ?? new TOptions();
        options.Validate();
        return options;
    }

    internal static bool IsOptionsKey(string key)
        => key.Split(':') is [var root, var modules, var name, var options, ..]
            && root.Equals("DigitalBrain", StringComparison.OrdinalIgnoreCase)
            && modules.Equals("Modules", StringComparison.OrdinalIgnoreCase)
            && options.Equals("Options", StringComparison.OrdinalIgnoreCase)
            && name.Length > 0 && !name.Contains("__", StringComparison.Ordinal) && !int.TryParse(name, out _);


    public static string OptionsKey(string moduleName) => IntoChatOSConfiguration.ModuleOptionsSection(moduleName);
}
