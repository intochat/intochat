namespace IntoChat.OS.Architecture.Tests.Unit;

// D-18: app modules reach storage only through the runtime broker. They do not name a storage neuron,
// and nothing in os calls the runtime access log. Recording lives in DigitalBrain.Runtime.Orleans.
public sealed class StorageAccessThroughBrokerFacts
{
    private static readonly string[] Neurons = ["LocalDiskNeuron", "DriveNeuron", "WindowsNeuron", "BlobStorageNeuron"];

    [Fact]
    public void AppModulesDoNotNameStorageNeurons()
    {
        var offenders = new List<string>();
        foreach (var project in Production())
        {
            var files = Repository.SourceFiles(project.Directory, "*.cs").Where(path => Owns(project, path)).ToList();
            if (!files.Any(file => File.ReadAllText(file).Contains("[App(", StringComparison.Ordinal))) { continue; }
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (var neuron in Neurons)
                {
                    if (text.Contains(neuron, StringComparison.Ordinal)) { offenders.Add($"{project.Name}: {Repository.Relative(file)} names {neuron}"); }
                }
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void ModulesDoNotReferenceTheAccessLog()
    {
        var offenders = new List<string>();
        foreach (var project in Production().Where(project => project.Name.StartsWith("IntoChat.OS.Modules", StringComparison.Ordinal)))
        {
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs").Where(path => Owns(project, path)))
            {
                if (File.ReadAllText(file).Contains("StorageAccessLog", StringComparison.Ordinal))
                {
                    offenders.Add($"{project.Name}: {Repository.Relative(file)}");
                }
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void OsAssembliesDoNotCallRecord()
    {
        var offenders = new List<string>();
        foreach (var project in Production())
        {
            foreach (var file in Repository.SourceFiles(project.Directory, "*.cs").Where(path => Owns(project, path)))
            {
                if (File.ReadAllText(file).Contains("StorageAccessLog.Record", StringComparison.Ordinal))
                {
                    offenders.Add($"{project.Name}: {Repository.Relative(file)}");
                }
            }
        }
        Assert.Empty(offenders);
    }

    private static IEnumerable<Repository.Project> Production()
        => Repository.Projects.Values.Where(project => !project.Name.Contains(".Tests", StringComparison.Ordinal));

    private static bool Owns(Repository.Project project, string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var owner = Repository.Projects.Values
            .Where(candidate => full.StartsWith(candidate.Directory + Path.DirectorySeparatorChar, comparison))
            .MaxBy(candidate => candidate.Directory.Length);
        return owner?.Name == project.Name;
    }
}
