using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Modules.AI.Tests.Scenarios;

// The contracts the shipped assistant app (Apps/Assistant beside the DigitalBrain.OS project) composes,
// read from its program and resolved to full names against the deployed contracts.
internal static class ShippedAssistant
{
    public static string[] Contracts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "DigitalBrain.slnx"))) { directory = directory.Parent; }
        var os = XDocument.Load(Path.Combine(directory.FullName, "DigitalBrain.slnx")).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(path => Path.GetFileNameWithoutExtension(path) == "DigitalBrain.OS");
        var lib = File.ReadAllText(Path.Combine(directory.FullName, Path.GetDirectoryName(os)!, "..", "Apps", "Assistant", "lib", "Assistant.cs"));
        var list = Regex.Match(lib, @"string\[\] Contracts = \[(?<types>[^\]]*)\];").Groups["types"].Value;
        var deployed = new ContractCatalog(ContractCatalog.Deployed()).Neurons;
        return [.. Regex.Matches(list, @"typeof\((?<name>\w+)\)").Select(match =>
            deployed.Single(name => name.EndsWith("." + match.Groups["name"].Value, StringComparison.Ordinal)))];
    }
}
