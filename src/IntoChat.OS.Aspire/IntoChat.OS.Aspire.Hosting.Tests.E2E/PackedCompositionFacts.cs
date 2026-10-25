using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace IntoChat.OS.Aspire.Hosting.Tests.E2E;

public sealed class PackedCompositionFacts
{
    [Fact(Timeout = 900_000)]
    public async Task PackagesComposeIsolatedBrainsAndReuseStorageAfterResourceRenaming()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(14));
        var ct = deadline.Token;
        var root = FindRepository();
        var scratch = Path.Combine(Path.GetTempPath(), "intochat-os-packages-" + Guid.NewGuid().ToString("N"));
        var feed = Path.Combine(scratch, "feed");
        Directory.CreateDirectory(feed);
        TestContext.Current.TestOutputHelper!.WriteLine("Package diagnostics: " + scratch);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var appHostSdk = XDocument.Load(ProjectPath(root, "IntoChat.OS.AppHost")).Root!.Attribute("Sdk")!.Value;
        const string version = "0.0.0-integration";
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            "IntoChat.OS.Testing",
            "IntoChat.OS.Testing.Aspire",
            "IntoChat.OS.Testing.Playwright",
            // The harness no longer drags modules in; one contracts package proves module
            // contracts stay consumable from the feed on their own.
            "IntoChat.OS.Modules.AI.Contracts",
            "IntoChat.OS.Client",
            "IntoChat.OS.Aspire.Server",
            "IntoChat.OS.Aspire.Client",
            // The composition's storage: the orleans module and its Azurite hosting.
            "IntoChat.OS.Modules.Microsoft.Orleans",
            "IntoChat.OS.Modules.Microsoft.Orleans.Aspire.Hosting",
        };
        foreach (var project in roots) { Collect(ProjectPath(root, project)); }
        foreach (var project in projects.Order())
        {
            await Run(root, "pack-" + Path.GetFileNameWithoutExtension(project), "pack", project, "-c", configuration,
                "--no-build", "-o", feed, "-p:PackageVersion=" + version, "-p:CodeGraphRefresh=false");
        }
        var packages = Directory.GetFiles(feed, "*.nupkg");
        Assert.NotEmpty(packages);
        foreach (var path in packages)
        {
            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "icon.png");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".Tests.dll", StringComparison.Ordinal)
                || entry.FullName.Contains(".Tests.", StringComparison.Ordinal));
            using var manifest = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var xml = XDocument.Load(manifest);
            var packageId = xml.Descendants().Single(element => element.Name.LocalName == "id").Value;
            foreach (var dependency in xml.Descendants().Where(element => element.Name.LocalName == "dependency"
                && element.Attribute("id")!.Value.StartsWith("IntoChat.OS", StringComparison.Ordinal)))
            {
                var id = dependency.Attribute("id")!.Value;
                Assert.True(File.Exists(Path.Combine(feed, id + "." + version + ".nupkg")), "Unpacked dependency: " + id);
                if (packageId == "IntoChat.OS" || packageId.EndsWith(".Contracts", StringComparison.Ordinal))
                { Assert.True(id == "IntoChat.OS" || id.EndsWith(".Contracts", StringComparison.Ordinal), $"Contract package {packageId} depends on implementation {id}."); }
                // The behavior client stays off the framework; the Orleans client runs beside a silo and uses it.
                if (packageId == "IntoChat.OS.Client")
                { Assert.DoesNotContain(id, new[] { "IntoChat.OS.Framework", "IntoChat.OS.Authority", "IntoChat.OS.Aspire.Server" }); }
                if (packageId == "IntoChat.OS.Client.Orleans")
                { Assert.DoesNotContain(id, new[] { "IntoChat.OS.Authority", "IntoChat.OS.Aspire.Server" }); }
            }
        }
        var consumer = Path.Combine(scratch, "consumer");
        Directory.CreateDirectory(consumer);
        // Stop discovery at the fixture boundary, even when a developer has build files in TEMP.
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        { await File.WriteAllTextAsync(Path.Combine(consumer, file), "<Project />", ct); }
        await File.WriteAllTextAsync(Path.Combine(consumer, ".editorconfig"), "root = true\n", ct);
        foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.cs.txt"))
        { File.Copy(source, Path.Combine(consumer, Path.GetFileNameWithoutExtension(source))); }
        await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <Sdk Name="{{appHostSdk.Split('/')[0]}}" Version="{{appHostSdk.Split('/')[1]}}" />
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AspireUseCliBundle>true</AspireUseCliBundle>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <RestorePackagesPath>{{Path.Combine(scratch, "packages")}}</RestorePackagesPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="IntoChat.OS.Testing.Playwright" Version="{{version}}" />
                <PackageReference Include="IntoChat.OS.Aspire.Server" Version="{{version}}" />
                <PackageReference Include="IntoChat.OS.Aspire.Client" Version="{{version}}" />
                <PackageReference Include="IntoChat.OS.Modules.Microsoft.Orleans" Version="{{version}}" />
                <PackageReference Include="IntoChat.OS.Modules.Microsoft.Orleans.Aspire.Hosting" Version="{{version}}" />
              </ItemGroup>
            </Project>
            """, ct);
        var nuget = new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed)),
            new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json")))));
        nuget.Save(Path.Combine(consumer, "NuGet.Config"));
        File.Copy(Path.Combine(root, "global.json"), Path.Combine(consumer, "global.json"));
        foreach (var (id, source) in new[]
        {
            ("IntoChat.OS.Modules.AI.Contracts", "_ = typeof(IntoChat.OS.Modules.AI.Models.ILLM);"),
            ("IntoChat.OS.Client", "_ = typeof(IntoChat.OS.Client.IntoChatOSClient);"),
            ("IntoChat.OS.Testing", "await using var brain = await IntoChat.OS.Testing.TestBrain.Create().StartAsync();"),
            ("IntoChat.OS.Aspire.Server", "var builder = global::Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(); IntoChat.OS.Aspire.Server.ServerHostingExtensions.AddIntoChatOSServer(builder, _ => { });"),
        })
        {
            var profile = Path.Combine(scratch, id);
            Directory.CreateDirectory(profile);
            foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", ".editorconfig", "NuGet.Config", "global.json" })
            { File.Copy(Path.Combine(consumer, file), Path.Combine(profile, file)); }
            var project = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk.Web"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net11.0"), new XElement("ImplicitUsings", "enable"),
                    new XElement("TreatWarningsAsErrors", "true"), new XElement("RestorePackagesPath", Path.Combine(scratch, "packages"))),
                new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", version)))));
            project.Save(Path.Combine(profile, "Profile.csproj"));
            await File.WriteAllTextAsync(Path.Combine(profile, "Program.cs"), source, ct);
            await Run(profile, "build-" + id, "build", "Profile.csproj", "-c", configuration, "-v", "quiet");
        }
        await Run(consumer, "build-consumer", "build", "Consumer.csproj", "-c", configuration, "-v", "quiet");
        await Run(consumer, "composition", Path.Combine(consumer, "bin", configuration, "net11.0", "Consumer.dll"));
        Directory.Delete(scratch, recursive: true);

        void Collect(string path)
        {
            path = Path.GetFullPath(path);
            if (!projects.Add(path)) { return; }
            var project = XDocument.Load(path);
            Assert.Contains(project.Descendants("IsPackable"), element => element.Value == "true");
            foreach (var reference in project.Descendants("ProjectReference"))
            { Collect(Path.Combine(Path.GetDirectoryName(path)!, reference.Attribute("Include")!.Value.Replace('\\', '/'))); }
        }

        async Task Run(string workingDirectory, string stage, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            DotNetTestProcess.DisableBuildServerReuse(start);
            foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
            using var process = Process.Start(start) ?? throw new IOException("dotnet did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(ct); }
            catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            finally { await File.WriteAllTextAsync(Path.Combine(scratch, stage + ".log"), await output + await error, CancellationToken.None); }
            Assert.True(process.ExitCode == 0, $"{stage} exited {process.ExitCode}. See {Path.Combine(scratch, stage + ".log")}.");
        }
    }

    // Projects are located through the solution by name, never by a path literal.
    private static string ProjectPath(string root, string name)
    {
        var entry = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx")).Descendants("Project")
            .Select(element => element.Attribute("Path")!.Value.Replace('\\', '/'))
            .Single(path => Path.GetFileNameWithoutExtension(path) == name);
        return Path.GetFullPath(Path.Combine(root, entry));
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx")))
        { directory = directory.Parent; }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Intochat.OS.slnx was not found.");
    }
}
