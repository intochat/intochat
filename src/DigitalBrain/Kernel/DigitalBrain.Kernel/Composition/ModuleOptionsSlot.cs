using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;

namespace DigitalBrain.Kernel;

internal abstract class ModuleOptionsSlot
{
    public abstract Type OptionsType { get; }
    public abstract ModuleDefinition Compile();
    public abstract ModuleOptionsSlot Clone();
    public abstract void Edit(Delegate configure);

    public static ModuleOptionsSlot? For(Type moduleType)
    {
        var optionsType = OptionsTypeOf(moduleType);
        return optionsType is null ? null
            : (ModuleOptionsSlot)Activator.CreateInstance(typeof(ModuleOptionsSlot<,>).MakeGenericType(moduleType, optionsType), [null])!;
    }

    // Options live on the Module<TOptions> base; a settings-free module derives Module directly.
    private static Type? OptionsTypeOf(Type moduleType)
    {
        for (var type = moduleType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Module<>))
            { return type.GetGenericArguments()[0]; }
        }
        return null;
    }
}

internal sealed class ModuleOptionsSlot<TModule, TOptions> : ModuleOptionsSlot
    where TModule : Module<TOptions>, new() where TOptions : class, IModuleOptions, new()
{
    private TOptions _options;
    public ModuleOptionsSlot(TOptions? options) => _options = options ?? new TOptions();
    public override Type OptionsType => typeof(TOptions);
    public override ModuleDefinition Compile() => ModuleOptionsSerialization.Compile<TModule, TOptions>(_options);
    public override ModuleOptionsSlot Clone() => new ModuleOptionsSlot<TModule, TOptions>(RoundTrip(_options));

    public override void Edit(Delegate configure)
    {
        var working = RoundTrip(_options);
        ((Action<TOptions>)configure)(working);
        _options = RoundTrip(working);
    }

    private static TOptions RoundTrip(TOptions options)
        => JsonSerializer.Deserialize<TOptions>(JsonSerializer.Serialize(options))!;
}
