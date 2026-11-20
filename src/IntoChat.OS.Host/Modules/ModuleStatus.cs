// os-only: os module activation status (licensing, enablement).
using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Host.Modules;

public enum ModuleState { Active, Unlicensed, Disabled }

public sealed record ModuleStatus(ModuleId Module, ModuleState State, string? Reason);
