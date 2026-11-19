using DigitalBrain.Modules;

namespace IntoChat.OS.Modules.UI;

// Unified stateful UI component neurons (Chart, Toggle, CheckBox, Select, Tabs, Expander, ListBox, Table).
[DigitalBrain.Modules.Module(UiModule.ModuleName)]
public sealed class UiModule : DigitalBrain.Modules.Module
{
    internal const string ModuleName = "ui";

    public static System.Reflection.Assembly? Contracts => typeof(Chart.IChart).Assembly;

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}
