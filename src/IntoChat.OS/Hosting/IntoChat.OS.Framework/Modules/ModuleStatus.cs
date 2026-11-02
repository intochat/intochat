// os-only: os module activation status (licensing, enablement).
namespace IntoChat.OS.Framework.Modules;

public enum ModuleState { Active, Unlicensed, Disabled }

public sealed record ModuleStatus(ModuleId Module, ModuleState State, string? Reason);
