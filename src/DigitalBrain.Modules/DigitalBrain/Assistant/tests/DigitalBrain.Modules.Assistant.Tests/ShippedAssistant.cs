using System.Text.RegularExpressions;

namespace DigitalBrain.Modules.Assistant.Tests;

// The tool list the shipped assistant app (src/DigitalBrain.OS/Apps/Assistant) asks for.
internal static class ShippedAssistant
{
    public static string[] Tools()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "DigitalBrain.slnx"))) { directory = directory.Parent; }
        var chat = File.ReadAllText(Path.Combine(directory.FullName, "src", "DigitalBrain.OS", "Apps", "Assistant", "synapses", "chat.cs"));
        var list = Regex.Match(chat, @"string\[\] Tools = \[(?<names>[^\]]*)\];").Groups["names"].Value;
        return [.. Regex.Matches(list, "\"(?<name>[^\"]+)\"").Select(match => match.Groups["name"].Value)];
    }
}
