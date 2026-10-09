using System.ComponentModel;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Catalog;
using ModelContextProtocol.Server;

namespace DigitalBrain.Microsoft.CSharp.Authoring;

// The contract discovery is registered by CSharpModule, so its absence means this host cannot run C# files.
public sealed class CSharpToolService(IDigitalBrain brain, IKernelView kernel, CSharpContractDiscovery? contracts = null, CSharpScriptCheck? check = null)
{
    public static readonly (MethodInfo Method, string Name)[] Tools = [.. typeof(ScopedCSharpTools).GetMethods()
        .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
        .Where(candidate => candidate.Tool is not null)
        .Select(candidate => (candidate.Method, candidate.Tool!.Name!))];

    public bool CanRun => contracts is not null;
    public CSharpContractDiscovery? Discovery => contracts;
    public CSharpScriptCheck? Check => check;

    public ScopedCSharpTools ForScope(string scope) => new(brain, kernel, contracts, scope, CanRun);
}
