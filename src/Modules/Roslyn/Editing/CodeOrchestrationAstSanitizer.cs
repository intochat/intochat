using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

/// <summary>
/// Roslyn C# AST sanitizer for Plane 1 code orchestration scripts.
/// Inspects syntax trees prior to compilation to reject forbidden namespaces
/// (<c>System.Reflection</c>, <c>System.Net.Sockets</c>, <c>System.Diagnostics.Process</c>, etc.),
/// unauthorized filesystem/process/network I/O, unsafe code, P/Invoke, direct Orleans references,
/// and authority contract access.
/// </summary>
internal sealed class CodeOrchestrationAstSanitizer : ICodeOrchestrationAstSanitizer
{
    private static readonly string[] AuthorityContractPrefixes =
    [
        "DigitalBrain.Confirmation.",
        "DigitalBrain.Capabilities.Identity.",
        "DigitalBrain.Policies.",
        "IntoChat.OS.Modules.Apps.CodeBlocks.IClientGrants",
        "IntoChat.OS.Modules.Apps.Approvals.",
        "IntoChat.OS.Modules.Identity.",
    ];

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Reflection",
        "System.Net.Sockets",
        "System.Net.Http",
        "System.Net.WebSockets",
        "System.Net.NetworkInformation",
        "System.Net.Mail",
        "System.Net.Security",
        "System.Diagnostics.Process",
        "System.Runtime.InteropServices",
        "System.Runtime.Loader",
        "System.Runtime.CompilerServices.Unsafe",
        "System.IO.Pipes",
        "System.IO.MemoryMappedFiles",
        "Microsoft.Orleans",
        "Orleans",
        "Microsoft.Win32",
    ];

    private static readonly string[] ForbiddenQualifiedTypePrefixes =
    [
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.FileStream",
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.FileSystemWatcher",
        "System.IO.DriveInfo",
        "System.IO.RandomAccess",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.Net.WebClient",
        "System.Net.WebRequest",
        "System.Net.HttpWebRequest",
        "System.Net.Dns",
        "System.Net.Sockets",
        "System.Net.Http.HttpClient",
        "System.Net.Http.SocketsHttpHandler",
        "System.Net.Http.HttpClientHandler",
        "System.Reflection",
        "System.Runtime.Loader",
        "System.Runtime.InteropServices",
        "System.AppDomain",
        "System.Activator",
    ];

    private static readonly HashSet<string> ForbiddenUnqualifiedTypes = new(StringComparer.Ordinal)
    {
        // Unauthorized filesystem I/O
        "File",
        "Directory",
        "FileInfo",
        "DirectoryInfo",
        "FileStream",
        "StreamReader",
        "StreamWriter",
        "FileSystemWatcher",
        "DriveInfo",
        "RandomAccess",
        "NamedPipeClientStream",
        "NamedPipeServerStream",
        "MemoryMappedFile",
        // Process execution & native interop
        "Process",
        "ProcessStartInfo",
        "Marshal",
        "NativeLibrary",
        // Direct networking / sockets
        "Socket",
        "TcpClient",
        "TcpListener",
        "UdpClient",
        "NetworkStream",
        "HttpClient",
        "SocketsHttpHandler",
        "HttpClientHandler",
        "WebClient",
        "WebRequest",
        "HttpWebRequest",
        "ClientWebSocket",
        "Dns",
        // Reflection & dynamic assembly loading
        "Assembly",
        string.Concat("Assembly", "LoadContext"),
        "AppDomain",
        "Activator",
        // Direct Orleans cluster access (Hard Invariant)
        string.Concat("IGrain", "Factory"),
        "IClusterClient",
        "SiloHost",
        "GrainId",
    };

    private static readonly HashSet<string> ForbiddenEnvironmentMembers = new(StringComparer.Ordinal)
    {
        "GetEnvironmentVariable",
        "GetEnvironmentVariables",
        "SetEnvironmentVariable",
        "Exit",
        "FailFast",
    };

    private static readonly HashSet<string> ForbiddenAttributes = new(StringComparer.Ordinal)
    {
        "DllImport",
        "DllImportAttribute",
        "LibraryImport",
        "LibraryImportAttribute",
        "UnmanagedCallersOnly",
        "UnmanagedCallersOnlyAttribute",
    };

    private static readonly HashSet<string> AuthorityShortNames = new(StringComparer.Ordinal)
    {
        "IConfirmations",
        "IConfirmationRequests",
        "IConfirmationInbox",
        "IClientGrants",
        "IApprovals",
        "IPolicies",
    };

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public AstSanitizationResult Validate(string source) => ValidateSource(source);

    public AstContractUsage InspectContractUsage(string source, IReadOnlyList<AstContractDescriptor> catalog)
        => InspectSourceContractUsage(source, catalog);

    internal static AstSanitizationResult ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new AstSanitizationResult(false, [new AstViolation("AST001", "Script source is empty.", 1, "")]);
        }

        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var violations = new List<AstViolation>();

        foreach (var diagnostic in tree.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                violations.Add(new AstViolation("AST001", $"Syntax error ({diagnostic.Id}): {diagnostic.GetMessage()}", line, ""));
            }
        }

        var root = tree.GetRoot();
        var walker = new SanitizerWalker(violations);
        walker.Visit(root);

        var deduped = violations
            .DistinctBy(static v => (v.RuleId, v.Line, v.Message))
            .OrderBy(static v => v.Line)
            .ThenBy(static v => v.RuleId, StringComparer.Ordinal)
            .ToArray();

        return new AstSanitizationResult(deduped.Length == 0, deduped);
    }

    internal static AstContractUsage InspectSourceContractUsage(string source, IReadOnlyList<AstContractDescriptor> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(source))
        {
            return new AstContractUsage([], []);
        }

        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var walker = new ContractUsageWalker(catalog);
        walker.Visit(tree.GetRoot());
        return new AstContractUsage(
            [.. walker.ReferencedContracts.Order(StringComparer.OrdinalIgnoreCase)],
            [.. walker.PersonOnlyCalls.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    private static bool IsAuthorityName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        return AuthorityContractPrefixes.Any(prefix =>
            trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, prefix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("global::", StringComparison.Ordinal))
        {
            text = text["global::".Length..];
        }

        return text;
    }

    private static bool MatchesPrefix(string value, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (string.Equals(value, prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int LineOf(SyntaxNode node)
        => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private sealed class SanitizerWalker(List<AstViolation> violations) : CSharpSyntaxWalker
    {
        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            var rawName = node.NamespaceOrType?.ToString() ?? node.Name?.ToString() ?? "";
            var name = NormalizeName(rawName);
            if (MatchesPrefix(name, ForbiddenNamespacePrefixes) || MatchesPrefix(name, ForbiddenQualifiedTypePrefixes))
            {
                Add("AST002", $"Forbidden namespace or type import '{rawName}'.", node);
            }
            else if (IsAuthorityName(name))
            {
                Add("AST005", $"Authority contract namespace '{rawName}' is forbidden in orchestration scripts.", node);
            }

            base.VisitUsingDirective(node);
        }

        public override void VisitQualifiedName(QualifiedNameSyntax node)
        {
            if (node.Parent is not QualifiedNameSyntax)
            {
                CheckTypeOrNamespaceReference(node.ToString(), node);
            }

            base.VisitQualifiedName(node);
        }

        public override void VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
        {
            if (node.Parent is not QualifiedNameSyntax)
            {
                CheckTypeOrNamespaceReference(node.ToString(), node);
            }

            base.VisitAliasQualifiedName(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var full = NormalizeName(node.ToString());
            if (node.Parent is not MemberAccessExpressionSyntax)
            {
                CheckTypeOrNamespaceReference(full, node);
            }

            var receiver = NormalizeName(node.Expression.ToString());
            var member = node.Name.Identifier.Text;

            if (ForbiddenUnqualifiedTypes.Contains(receiver))
            {
                Add("AST003", $"Unauthorized API access '{receiver}.{member}'. Use scoped Neuron contracts via os.Get<TContract>(key) instead.", node);
            }
            else if (receiver is "Environment" or "System.Environment" && ForbiddenEnvironmentMembers.Contains(member))
            {
                Add("AST003", $"Restricted environment access 'Environment.{member}' is forbidden in orchestration scripts.", node);
            }
            else if (receiver is "Type" or "System.Type" && member == "GetType")
            {
                Add("AST003", "Dynamic reflection via 'Type.GetType' is forbidden in orchestration scripts.", node);
            }
            else if (member is "GetType" or "GetMethod" or "GetProperty" or "GetField" or "GetConstructor"
                && receiver.Contains("typeof(", StringComparison.Ordinal))
            {
                Add("AST003", $"Reflection member access '{node}' is forbidden in orchestration scripts.", node);
            }

            base.VisitMemberAccessExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var typeName = NormalizeName(node.Type.ToString());
            var simpleName = ExtractSimpleTypeName(typeName);
            if (ForbiddenUnqualifiedTypes.Contains(simpleName) || MatchesPrefix(typeName, ForbiddenQualifiedTypePrefixes))
            {
                Add("AST003", $"Instantiating restricted type '{node.Type}' is forbidden in orchestration scripts.", node);
            }

            base.VisitObjectCreationExpression(node);
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            var id = node.Identifier.Text;
            if (AuthorityShortNames.Contains(id))
            {
                Add("AST005", $"Authority contract '{id}' cannot be referenced in orchestration scripts.", node);
            }
            else if (node.Parent is TypeArgumentListSyntax or VariableDeclarationSyntax or ParameterSyntax or CastExpressionSyntax or TypeOfExpressionSyntax
                && ForbiddenUnqualifiedTypes.Contains(id))
            {
                Add("AST003", $"Restricted type '{id}' is forbidden in orchestration scripts.", node);
            }

            base.VisitIdentifierName(node);
        }

        public override void VisitAttribute(AttributeSyntax node)
        {
            var name = ExtractSimpleTypeName(NormalizeName(node.Name.ToString()));
            if (ForbiddenAttributes.Contains(name))
            {
                Add("AST004", $"Native interop attribute '[{node.Name}]' is forbidden in orchestration scripts.", node);
            }

            base.VisitAttribute(node);
        }

        public override void VisitUnsafeStatement(UnsafeStatementSyntax node)
        {
            Add("AST004", "Unsafe blocks are forbidden in orchestration scripts.", node);
            base.VisitUnsafeStatement(node);
        }

        public override void VisitFixedStatement(FixedStatementSyntax node)
        {
            Add("AST004", "Fixed pointer statements are forbidden in orchestration scripts.", node);
            base.VisitFixedStatement(node);
        }

        public override void VisitPointerType(PointerTypeSyntax node)
        {
            Add("AST004", $"Pointer type '{node}' is forbidden in orchestration scripts.", node);
            base.VisitPointerType(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            CheckUnsafeOrExternModifiers(node.Modifiers, node);
            base.VisitMethodDeclaration(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            CheckUnsafeOrExternModifiers(node.Modifiers, node);
            base.VisitClassDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            CheckUnsafeOrExternModifiers(node.Modifiers, node);
            base.VisitStructDeclaration(node);
        }

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            CheckUnsafeOrExternModifiers(node.Modifiers, node);
            base.VisitLocalFunctionStatement(node);
        }

        private void CheckUnsafeOrExternModifiers(SyntaxTokenList modifiers, SyntaxNode node)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier.IsKind(SyntaxKind.UnsafeKeyword))
                {
                    Add("AST004", "The 'unsafe' modifier is forbidden in orchestration scripts.", node);
                }
                else if (modifier.IsKind(SyntaxKind.ExternKeyword))
                {
                    Add("AST004", "The 'extern' modifier (P/Invoke) is forbidden in orchestration scripts.", node);
                }
            }
        }

        private void CheckTypeOrNamespaceReference(string raw, SyntaxNode node)
        {
            var normalized = NormalizeName(raw);
            if (MatchesPrefix(normalized, ForbiddenNamespacePrefixes))
            {
                Add("AST002", $"Forbidden namespace reference '{raw}'.", node);
            }
            else if (MatchesPrefix(normalized, ForbiddenQualifiedTypePrefixes))
            {
                Add("AST003", $"Unauthorized API reference '{raw}'.", node);
            }
            else if (IsAuthorityName(normalized))
            {
                Add("AST005", $"Authority contract reference '{raw}' is forbidden in orchestration scripts.", node);
            }
        }

        private void Add(string ruleId, string message, SyntaxNode node)
        {
            var snippet = node.ToString();
            if (snippet.Length > 120)
            {
                snippet = snippet[..120] + "...";
            }

            violations.Add(new AstViolation(ruleId, message, LineOf(node), snippet));
        }

        private static string ExtractSimpleTypeName(string typeName)
        {
            var generic = typeName.IndexOf('<', StringComparison.Ordinal);
            var trimmed = generic >= 0 ? typeName[..generic] : typeName;
            var dot = trimmed.LastIndexOf('.');
            return dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
        }
    }

    private sealed class ContractUsageWalker(IReadOnlyList<AstContractDescriptor> catalog) : CSharpSyntaxWalker
    {
        private readonly Dictionary<string, AstContractDescriptor> _variableContracts = new(StringComparer.Ordinal);
        public HashSet<string> ReferencedContracts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PersonOnlyCalls { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void VisitVariableDeclarator(VariableDeclaratorSyntax node)
        {
            if (node.Initializer?.Value is InvocationExpressionSyntax invocation
                && TryResolveContractFromGetCall(invocation) is { } contract)
            {
                _variableContracts[node.Identifier.Text] = contract;
            }
            else if (node.Initializer?.Value is MemberAccessExpressionSyntax access
                && TryResolveSdkPropertyContract(access) is { } sdkContract)
            {
                _variableContracts[node.Identifier.Text] = sdkContract;
            }

            base.VisitVariableDeclarator(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (TryResolveSdkPropertyContract(node) is { } sdkContract)
            {
                ReferencedContracts.Add(sdkContract.Alias);
            }

            base.VisitMemberAccessExpression(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (TryResolveContractFromGetCall(node) is { } fetchedContract)
            {
                ReferencedContracts.Add(fetchedContract.Alias);
            }

            if (node.Expression is MemberAccessExpressionSyntax callAccess)
            {
                var methodName = callAccess.Name.Identifier.Text;
                AstContractDescriptor? targetContract = null;

                if (callAccess.Expression is IdentifierNameSyntax variable
                    && _variableContracts.TryGetValue(variable.Identifier.Text, out var bound))
                {
                    targetContract = bound;
                }
                else if (callAccess.Expression is InvocationExpressionSyntax chainedGet
                    && TryResolveContractFromGetCall(chainedGet) is { } inlineContract)
                {
                    targetContract = inlineContract;
                }
                else if (callAccess.Expression is MemberAccessExpressionSyntax sdkProp
                    && TryResolveSdkPropertyContract(sdkProp) is { } sdkContract)
                {
                    targetContract = sdkContract;
                }
                else if (callAccess.Expression is InvocationExpressionSyntax folderCall
                    && folderCall.Expression is MemberAccessExpressionSyntax folderAccess
                    && folderAccess.Name.Identifier.Text == "Folder")
                {
                    ReferencedContracts.Add("folder");
                    targetContract = FindCatalogContract("folder");
                }

                if (targetContract is not null)
                {
                    ReferencedContracts.Add(targetContract.Alias);
                    if (targetContract.PersonOnlyMethods.Contains(methodName, StringComparer.Ordinal))
                    {
                        PersonOnlyCalls.Add($"{targetContract.ShortName}.{methodName}");
                    }
                }
            }

            base.VisitInvocationExpression(node);
        }

        private AstContractDescriptor? TryResolveContractFromGetCall(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access)
            {
                return null;
            }

            if (access.Name is not GenericNameSyntax generic
                || generic.Identifier.Text is not ("Get" or "Slot")
                || generic.TypeArgumentList.Arguments.Count != 1)
            {
                return null;
            }

            var typeName = NormalizeName(generic.TypeArgumentList.Arguments[0].ToString());
            var matched = FindCatalogContract(typeName);
            if (matched is not null)
            {
                return matched;
            }

            ReferencedContracts.Add(typeName);
            return null;
        }

        private AstContractDescriptor? TryResolveSdkPropertyContract(MemberAccessExpressionSyntax access)
        {
            var receiver = access.Expression.ToString();
            if (receiver is not ("os" or "Brain" or "brain" or "this.Brain"))
            {
                return null;
            }

            return access.Name.Identifier.Text switch
            {
                "Notifications" => FindCatalogContract("notifications"),
                "Ai" => FindCatalogContract("ai"),
                "Tables" => FindCatalogContract("tables"),
                "Folder" => FindCatalogContract("folder"),
                _ => null,
            };
        }

        private AstContractDescriptor? FindCatalogContract(string name)
        {
            foreach (var entry in catalog)
            {
                if (string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.ShortName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Alias, name, StringComparison.OrdinalIgnoreCase)
                    || entry.AllAliases.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }
    }
}
