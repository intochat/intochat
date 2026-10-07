using System.Text.RegularExpressions;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;

namespace DigitalBrain.Modules.AI.Tests.Scenarios;

// The contracts the shipped assistant app (src/DigitalBrain.OS/Apps/Assistant) composes, read from
// its program and resolved to full names against the deployed contracts.
internal static class ShippedAssistant
{
    public static string[] Contracts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "DigitalBrain.slnx"))) { directory = directory.Parent; }
        var chat = File.ReadAllText(Path.Combine(directory.FullName, "src", "DigitalBrain.OS", "Apps", "Assistant", "synapses", "chat.cs"));
        var list = Regex.Match(chat, @"string\[\] Contracts = \[(?<types>[^\]]*)\];").Groups["types"].Value;
        var deployed = new NeuronContracts(NeuronContracts.Deployed()).Names;
        return [.. Regex.Matches(list, @"typeof\((?<name>\w+)\)").Select(match =>
            deployed.Single(name => name.EndsWith("." + match.Groups["name"].Value, StringComparison.Ordinal)))];
    }
}
