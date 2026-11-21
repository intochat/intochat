using DigitalBrain.Neurons;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Dynamic;

[Neuron("roslyn.codebase-tree")]
internal sealed class CodebaseTreeNeuron : Neuron<CodebaseTreeState>, ICodebaseTree
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "artifacts", ".dart_tool", "build", ".vs", ".idea",
    };

    public async Task<CodebaseTreeState> OpenRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Workspace root not found: {fullRoot}");
        }

        var rootNode = new CodebaseTreeNode(
            Id: "root",
            Label: Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ParentId: null,
            Kind: "workspace",
            IsFolder: true,
            ChildrenLoaded: true,
            RelativePath: "");

        var nodes = new List<CodebaseTreeNode> { rootNode };
        nodes.AddRange(ReadDirectoryChildren(fullRoot, fullRoot, parentId: "root"));

        var next = new CodebaseTreeState(
            Version: State.Version + 1,
            RootPath: fullRoot,
            Nodes: [.. nodes],
            ExpandedIds: ["root"],
            ActiveQuery: null);

        await Save(next, new CodebaseTreeUpdated(Id.FullKey(), next.Version, next.Nodes.Length, next.ActiveQuery));
        return next;
    }

    public async Task<CodebaseTreeState> ExpandNode(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var nodes = (State.Nodes ?? []).ToList();
        var index = nodes.FindIndex(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal));
        if (index < 0)
        {
            return State;
        }

        var target = nodes[index];
        var expanded = new HashSet<string>(State.ExpandedIds ?? [], StringComparer.Ordinal) { nodeId };

        if (!target.ChildrenLoaded && !string.IsNullOrWhiteSpace(State.RootPath) && target.RelativePath is not null)
        {
            var fullPath = Path.Combine(State.RootPath, target.RelativePath);
            if (target.Kind == "folder" && Directory.Exists(fullPath))
            {
                nodes.AddRange(ReadDirectoryChildren(State.RootPath, fullPath, parentId: target.Id));
                nodes[index] = target with { ChildrenLoaded = true };
            }
            else if (target.Kind == "csharp" && File.Exists(fullPath))
            {
                nodes.AddRange(ParseCSharpSymbols(fullPath, target.Id, target.RelativePath));
                nodes[index] = target with { ChildrenLoaded = true };
            }
        }

        var next = State with
        {
            Version = State.Version + 1,
            Nodes = [.. nodes],
            ExpandedIds = [.. expanded],
        };

        await Save(next, new CodebaseTreeUpdated(Id.FullKey(), next.Version, next.Nodes.Length, next.ActiveQuery));
        return next;
    }

    public async Task<CodebaseTreeState> QueryTopic(string topic, int maxMatches = 25)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (string.IsNullOrWhiteSpace(State.RootPath) || !Directory.Exists(State.RootPath))
        {
            return State;
        }

        var needle = topic.Trim();
        var fullRoot = State.RootPath;
        var nodeMap = new Dictionary<string, CodebaseTreeNode>(StringComparer.Ordinal);
        var expanded = new HashSet<string>(StringComparer.Ordinal) { "root" };

        nodeMap["root"] = new CodebaseTreeNode(
            Id: "root",
            Label: Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ParentId: null,
            Kind: "workspace",
            IsFolder: true,
            ChildrenLoaded: true,
            RelativePath: "");

        var matchCount = 0;
        foreach (var file in EnumerateWorkspaceFiles(fullRoot))
        {
            if (matchCount >= maxMatches) { break; }
            var rel = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var pathMatch = rel.Contains(needle, StringComparison.OrdinalIgnoreCase);

            List<CodebaseTreeNode>? symbolMatches = null;
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var symbols = ParseCSharpSymbols(file, $"file:{rel}", rel);
                var filtered = symbols.Where(s => s.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count > 0)
                {
                    symbolMatches = filtered;
                }
            }

            if (!pathMatch && symbolMatches is null)
            {
                continue;
            }

            EnsureAncestorFolders(rel, nodeMap, expanded);
            var fileId = $"file:{rel}";
            var parentDir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
            var parentId = string.IsNullOrEmpty(parentDir) ? "root" : $"dir:{parentDir}";
            var isCSharp = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

            nodeMap[fileId] = new CodebaseTreeNode(
                Id: fileId,
                Label: Path.GetFileName(file),
                ParentId: parentId,
                Kind: isCSharp ? "csharp" : "file",
                IsFolder: isCSharp,
                ChildrenLoaded: symbolMatches is not null,
                RelativePath: rel);

            if (symbolMatches is not null)
            {
                expanded.Add(fileId);
                foreach (var sym in symbolMatches)
                {
                    nodeMap[sym.Id] = sym;
                }
            }

            matchCount++;
        }

        var next = State with
        {
            Version = State.Version + 1,
            Nodes = [.. nodeMap.Values],
            ExpandedIds = [.. expanded],
            ActiveQuery = needle,
        };

        await Save(next, new CodebaseTreeUpdated(Id.FullKey(), next.Version, next.Nodes.Length, next.ActiveQuery));
        return next;
    }

    public Task<CodebaseTreeState> Read() => Task.FromResult(State);

    private static IEnumerable<CodebaseTreeNode> ReadDirectoryChildren(string rootPath, string dirPath, string parentId)
    {
        var results = new List<CodebaseTreeNode>();

        foreach (var dir in Directory.EnumerateDirectories(dirPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (IgnoredDirs.Contains(name)) { continue; }
            var rel = Path.GetRelativePath(rootPath, dir).Replace('\\', '/');
            results.Add(new CodebaseTreeNode(
                Id: $"dir:{rel}",
                Label: name,
                ParentId: parentId,
                Kind: "folder",
                IsFolder: true,
                ChildrenLoaded: false,
                RelativePath: rel));
        }

        foreach (var file in Directory.EnumerateFiles(dirPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            var isCSharp = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            results.Add(new CodebaseTreeNode(
                Id: $"file:{rel}",
                Label: name,
                ParentId: parentId,
                Kind: isCSharp ? "csharp" : "file",
                IsFolder: isCSharp,
                ChildrenLoaded: false,
                RelativePath: rel));
        }

        return results;
    }

    private static List<CodebaseTreeNode> ParseCSharpSymbols(string filePath, string fileNodeId, string relPath)
    {
        var results = new List<CodebaseTreeNode>();
        var source = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeName = typeDecl.Identifier.Text;
            var typeId = $"sym:{relPath}:{typeName}";
            var kind = typeDecl switch
            {
                InterfaceDeclarationSyntax => "interface",
                RecordDeclarationSyntax => "record",
                StructDeclarationSyntax => "struct",
                _ => "class",
            };

            results.Add(new CodebaseTreeNode(
                Id: typeId,
                Label: $"{kind} {typeName}",
                ParentId: fileNodeId,
                Kind: kind,
                IsFolder: false,
                ChildrenLoaded: true,
                RelativePath: relPath));

            foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodName = method.Identifier.Text;
                results.Add(new CodebaseTreeNode(
                    Id: $"sym:{relPath}:{typeName}.{methodName}",
                    Label: $"{methodName}()",
                    ParentId: typeId,
                    Kind: "method",
                    IsFolder: false,
                    ChildrenLoaded: true,
                    RelativePath: relPath));
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                yield return file;
            }

            foreach (var dir in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(dir);
                if (!IgnoredDirs.Contains(name))
                {
                    stack.Push(dir);
                }
            }
        }
    }

    private static void EnsureAncestorFolders(
        string relFilePath,
        Dictionary<string, CodebaseTreeNode> nodeMap,
        HashSet<string> expanded)
    {
        var parts = relFilePath.Split('/');
        var currentPath = "";
        var parentId = "root";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            currentPath = i == 0 ? parts[i] : $"{currentPath}/{parts[i]}";
            var dirId = $"dir:{currentPath}";
            if (!nodeMap.ContainsKey(dirId))
            {
                nodeMap[dirId] = new CodebaseTreeNode(
                    Id: dirId,
                    Label: parts[i],
                    ParentId: parentId,
                    Kind: "folder",
                    IsFolder: true,
                    ChildrenLoaded: true,
                    RelativePath: currentPath);
            }

            expanded.Add(dirId);
            parentId = dirId;
        }
    }
}
