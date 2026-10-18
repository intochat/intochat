using System.Text.RegularExpressions;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Modules.AI.Tests.Integration.Scenarios;

// The contracts the os Ino feature (features/os/Ino beside the solution) composes,
// read from its program and resolved to full names against the deployed contracts.
internal static class ShippedAssistant
{
    public static string[] Contracts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "DigitalBrain.slnx"))) { directory = directory.Parent; }
        var lib = File.ReadAllText(Path.Combine(directory.FullName, "features", "os", "Ino", "Ino.cs"));
        var list = Regex.Match(lib, @"string\[\] Contracts = \[(?<types>[^\]]*)\];").Groups["types"].Value;
        // The contract assemblies built beside this test stand in for the deployed host's compiled layer.
        var deployed = new DigitalBrain.Framework.CompiledTypes(Directory.GetFiles(AppContext.BaseDirectory, "DigitalBrain.Modules.*.Contracts.dll")
                .Select(path => System.Reflection.Assembly.Load(System.Reflection.AssemblyName.GetAssemblyName(path)))
                .Append(typeof(DigitalBrain.INeuron).Assembly).Append(typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly))
            .Entries.Where(type => type.Kind == DigitalBrain.TypeKind.Neuron).Select(type => type.Name).ToArray();
        return [.. Regex.Matches(list, @"typeof\((?<name>\w+)\)").Select(match =>
            deployed.Single(name => name.EndsWith("." + match.Groups["name"].Value, StringComparison.Ordinal)))];
    }
}
