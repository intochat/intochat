using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DigitalBrain.Modules;
using IntoChat.OS.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Architecture.Tests.Unit;

/// <summary>Enforces all 22 OS Rearchitecture House Rules with zero temporary exclusions.</summary>
public sealed class HouseRules22Facts
{
    private static readonly Lazy<Dictionary<string, string>> ProjectDirs = new(LoadProjectDirs);

    private static readonly Lazy<IReadOnlyList<ParsedSource>> ProductionSources = new(
        () => ParseTree(ProjectDirs.Value.Where(p => !p.Key.Contains(".Tests", StringComparison.Ordinal)).ToList()));

    private static readonly Lazy<IReadOnlyList<ParsedSource>> AllSourcesAndTests = new(
        () => ParseTree(ProjectDirs.Value.ToList()));

    private sealed record ParsedSource(string ProjectName, string FullPath, string FileName, string[] PathSegments, string Text, SyntaxTree Tree, CompilationUnitSyntax Root);

    private static Dictionary<string, string> LoadProjectDirs()
    {
        var slnx = XDocument.Load(Path.Combine(Repository.Root, "Intochat.OS.slnx"));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proj in slnx.Descendants("Project")
            .Where(proj => !Repository.IsFrameworkEntry(proj) && !Repository.IsFeatureEntry(proj)))
        {
            var rel = proj.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(rel))
            {
                continue;
            }
            var full = Path.GetFullPath(Path.Combine(Repository.Root, rel));
            var name = Path.GetFileNameWithoutExtension(full);
            var dir = Path.GetDirectoryName(full)!;
            map[name] = dir;
        }
        return map;
    }

    private static IReadOnlyList<ParsedSource> ParseTree(IReadOnlyList<KeyValuePair<string, string>> projects)
    {
        var list = new List<ParsedSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (projectName, dir) in projects)
        {
            foreach (var file in Repository.SourceFiles(dir, "*.cs"))
            {
                var full = Path.GetFullPath(file);
                if (!seen.Add(full))
                {
                    continue;
                }
                var text = File.ReadAllText(full);
                var tree = CSharpSyntaxTree.ParseText(text, path: full);
                var rel = Path.GetRelativePath(dir, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                list.Add(new ParsedSource(projectName, full, Path.GetFileName(full), rel, text, tree, tree.GetCompilationUnitRoot()));
            }
        }
        return list;
    }

    // Module code lives under src/Modules (IntoChat.OS.Modules.<Name>) or src/Components (IntoChat.<Name>): the move to
    // components must not take code out of a ratchet.
    private static bool IsModuleCode(ParsedSource source)
        => source.ProjectName.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
            || Repository.IsUnder(source.FullPath, Repository.ComponentsRoot);

    private static CompilationUnitSyntax ParseSample(string code, CancellationToken ct)
        => CSharpSyntaxTree.ParseText(code, cancellationToken: ct).GetCompilationUnitRoot(ct);

    private static bool IsNeuronOrScreenClass(ClassDeclarationSyntax cls)
    {
        var hasNeuronAttr = cls.AttributeLists
            .SelectMany(a => a.Attributes)
            .Any(a => a.Name.ToString() is "Neuron" or "DigitalBrain.Neuron" or "DigitalBrain.Neurons.Neuron");
        if (hasNeuronAttr)
        {
            return true;
        }
        if (cls.BaseList is null)
        {
            return false;
        }
        foreach (var baseType in cls.BaseList.Types)
        {
            var name = baseType.Type.ToString();
            if (name is "Neuron" or "DigitalBrain.Neuron" or "DigitalBrain.Neurons.Neuron"
                || name.StartsWith("Neuron<", StringComparison.Ordinal)
                || name.StartsWith("DigitalBrain.Neurons.Neuron<", StringComparison.Ordinal)
                || name.StartsWith("Screen<", StringComparison.Ordinal)
                || name.StartsWith("DigitalBrain.Screen<", StringComparison.Ordinal)
                || name.StartsWith("AggregateScreen<", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string DisplayPath(ParsedSource src) => Repository.Relative(src.FullPath).Replace('\\', '/');

    // Rule #1: No swallowed exceptions.
    [Fact]
    public void HouseRule01_NoSwallowedExceptionsActive()
    {
        var checks = typeof(SwallowFacts).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(m => m.IsDefined(typeof(FactAttribute), false) || m.IsDefined(typeof(TheoryAttribute), false));
        Assert.True(checks >= 5, $"Expected SwallowFacts to guard all catch blocks; found {checks} test methods.");
    }

    // Rule #2: LoggerFactory.Create restricted to OsConsoleLog & SandboxConsoleLog; zero *Fallback classes;
    // zero `?? new <DI-registered-service>` fallbacks.
    [Fact]
    public void HouseRule02_NoAdHocLoggerFactoriesOrFallbackClasses()
    {
        var allowedLogFiles = new HashSet<(string Project, string File)>
        {
            ("IntoChat.OS.Host", "OsConsoleLog.cs"),
            ("IntoChat.OS.Sandbox", "SandboxConsoleLog.cs"),
        };
        var registeredServices = DiscoverRegisteredServiceTypes(ProductionSources.Value.Select(s => s.Root));
        var badLoggers = new List<string>();
        var fallbacks = new List<string>();
        var coalesceFallbacks = new List<string>();

        foreach (var src in ProductionSources.Value)
        {
            var lines = FindAdHocLoggerFactoryLines(src.Root);
            if (lines.Count > 0 && !allowedLogFiles.Contains((src.ProjectName, src.FileName)))
            {
                foreach (var line in lines)
                {
                    badLoggers.Add($"{DisplayPath(src)}:{line}");
                }
            }

            foreach (var fallback in FindFallbackClasses(src.Root))
            {
                fallbacks.Add($"{DisplayPath(src)}: {fallback}");
            }

            foreach (var (line, typeName) in FindServiceCoalesceFallbacks(src.Root, registeredServices))
            {
                coalesceFallbacks.Add($"{DisplayPath(src)}:{line}: ?? new {typeName}");
            }
        }

        Assert.True(badLoggers.Count == 0, "Ad-hoc LoggerFactory.Create outside OsConsoleLog/SandboxConsoleLog:\n" + string.Join('\n', badLoggers));
        Assert.True(fallbacks.Count == 0, "Forbidden *Fallback* classes in production source:\n" + string.Join('\n', fallbacks));
        Assert.True(coalesceFallbacks.Count == 0, "Forbidden `?? new` fallback instantiating a DI/module-registered service:\n" + string.Join('\n', coalesceFallbacks));
    }

    [Fact]
    public void HouseRule02_ChallengerCatchesAdHocLoggerFactoryAndFallback()
    {
        var sample = ParseSample("""
            class BadFallback { }
            class X { void M() { var f = LoggerFactory.Create(b => {}); } }
            """, TestContext.Current.CancellationToken);
        Assert.Single(FindAdHocLoggerFactoryLines(sample));
        Assert.Equal(["BadFallback"], FindFallbackClasses(sample));
    }

    [Fact]
    public void HouseRule02_ChallengerCatchesCoalescingModuleService()
    {
        var sample = ParseSample("""
            class SampleModule
            {
                void Configure(IServiceCollection services)
                {
                    services.AddSingleton<IMyModule, MemoryMyModule>();
                    services.TryAddScoped<LocalStore>();
                    services.AddSingleton<MyOptions>();
                }
                void Call(IMyModule? maybe, LocalStore? store, Dictionary<string, int>? map, MyOptions? opts)
                {
                    var a = maybe ?? new MemoryMyModule();
                    IMyModule b = maybe ?? new();
                    var c = store ?? new LocalStore();
                    var okMap = map ?? new Dictionary<string, int>();
                    var okOpts = opts ?? new MyOptions();
                }
            }
            """, TestContext.Current.CancellationToken);
        var registered = DiscoverRegisteredServiceTypes([sample]);
        Assert.Contains("IMyModule", registered);
        Assert.Contains("MemoryMyModule", registered);
        Assert.Contains("LocalStore", registered);
        Assert.DoesNotContain("MyOptions", registered);
        var hits = FindServiceCoalesceFallbacks(sample, registered);
        Assert.Equal(3, hits.Count);
    }

    private static List<int> FindAdHocLoggerFactoryLines(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text == "Create"
                && ma.Expression.ToString().EndsWith("LoggerFactory", StringComparison.Ordinal))
            .Select(inv => inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    private static List<string> FindFallbackClasses(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text.Contains("Fallback", StringComparison.Ordinal))
            .Select(t => t.Identifier.Text)];

    private static HashSet<string> DiscoverRegisteredServiceTypes(IEnumerable<CompilationUnitSyntax> roots)
    {
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var methodName = inv.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name,
                    SimpleNameSyntax sn => sn,
                    _ => null,
                };
                if (methodName is null || methodName.Identifier.Text is not (
                    "AddSingleton" or "TryAddSingleton" or
                    "AddScoped" or "TryAddScoped" or
                    "AddTransient" or "TryAddTransient" or
                    "AddHostedService"))
                {
                    continue;
                }

                if (methodName is GenericNameSyntax gen)
                {
                    foreach (var arg in gen.TypeArgumentList.Arguments)
                    {
                        AddRegisteredTypeName(registered, ExtractSimpleTypeName(arg));
                    }
                }

                foreach (var arg in inv.ArgumentList.Arguments)
                {
                    if (arg.Expression is TypeOfExpressionSyntax typeOf)
                    {
                        AddRegisteredTypeName(registered, ExtractSimpleTypeName(typeOf.Type));
                    }
                }
            }
        }
        return registered;

        static void AddRegisteredTypeName(HashSet<string> set, string? name)
        {
            if (string.IsNullOrEmpty(name)
                || name.EndsWith("Options", StringComparison.Ordinal)
                || name is "IHostedService" or "IOptions" or "IPostConfigureOptions" or "IConfigureOptions" or "TimeProvider" or "Object" or "String" or "Dictionary")
            {
                return;
            }
            set.Add(name);
        }
    }

    private static List<(int Line, string TypeName)> FindServiceCoalesceFallbacks(
        CompilationUnitSyntax root,
        IReadOnlySet<string> registeredServiceTypes)
    {
        var hits = new List<(int Line, string TypeName)>();
        foreach (var bin in root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.CoalesceExpression)))
        {
            var right = UnwrapParens(bin.Right);
            string? targetType = right switch
            {
                ObjectCreationExpressionSyntax oc => ExtractSimpleTypeName(oc.Type),
                ImplicitObjectCreationExpressionSyntax => InferCoalesceTargetTypeName(bin),
                _ => null,
            };
            if (targetType is not null && registeredServiceTypes.Contains(targetType))
            {
                var line = bin.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                hits.Add((line, targetType));
            }
        }
        return hits;

        static ExpressionSyntax UnwrapParens(ExpressionSyntax expr)
        {
            while (expr is ParenthesizedExpressionSyntax paren)
            {
                expr = paren.Expression;
            }
            return expr;
        }

        static string? InferCoalesceTargetTypeName(BinaryExpressionSyntax bin)
        {
            if (bin.Parent is EqualsValueClauseSyntax eq)
            {
                if (eq.Parent is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax varDecl } && !varDecl.Type.IsVar)
                {
                    return ExtractSimpleTypeName(varDecl.Type);
                }
                if (eq.Parent is PropertyDeclarationSyntax propDecl)
                {
                    return ExtractSimpleTypeName(propDecl.Type);
                }
            }
            if (bin.Parent is ArrowExpressionClauseSyntax arrow)
            {
                if (arrow.Parent is PropertyDeclarationSyntax propDecl)
                {
                    return ExtractSimpleTypeName(propDecl.Type);
                }
                if (arrow.Parent is MethodDeclarationSyntax methodDecl)
                {
                    return ExtractSimpleTypeName(methodDecl.ReturnType);
                }
            }
            if (bin.Parent is ReturnStatementSyntax)
            {
                var enclosingMethod = bin.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (enclosingMethod is not null)
                {
                    return ExtractSimpleTypeName(enclosingMethod.ReturnType);
                }
            }
            return null;
        }
    }

    // Rule #3: Zero InProcessCallerScope, .ToCaller(, CallerExtensions, or BindingFlags.NonPublic targeting DigitalBrain.
    [Fact]
    public void HouseRule03_NoCallerCompatBridgesOrDigitalBrainNonPublicReflection()
    {
        Assert.False(File.Exists(Path.Combine(ProjectDirs.Value["IntoChat.OS.Modules.Identity"], "Callers", "CallerExtensions.cs")));

        var bannedScope = "InProcessCaller" + "Scope";
        var bannedToCaller = ".To" + "Caller(";
        var nonPublicToken = "BindingFlags." + "NonPublic";
        var digitalBrainPrefix = "Digital" + "Brain";

        var violations = new List<string>();
        foreach (var src in AllSourcesAndTests.Value)
        {
            if (string.Equals(src.ProjectName, "IntoChat.OS.Architecture.Tests.Unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (src.Text.Contains(bannedScope, StringComparison.Ordinal))
            {
                violations.Add($"{DisplayPath(src)}: contains {bannedScope}");
            }
            if (src.Text.Contains(bannedToCaller, StringComparison.Ordinal))
            {
                violations.Add($"{DisplayPath(src)}: contains {bannedToCaller}");
            }
            if (src.Text.Contains(nonPublicToken, StringComparison.Ordinal)
                && HasDigitalBrainNonPublicReflection(src.Root, digitalBrainPrefix))
            {
                violations.Add($"{DisplayPath(src)}: BindingFlags.NonPublic targeting DigitalBrain");
            }
        }

        Assert.True(violations.Count == 0, "Caller bridge or non-public DigitalBrain reflection violations:\n" + string.Join('\n', violations));
    }

    private static bool HasDigitalBrainNonPublicReflection(CompilationUnitSyntax root, string digitalBrainPrefix)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var text = inv.ToString();
            if (!text.Contains("NonPublic", StringComparison.Ordinal))
            {
                continue;
            }
            var target = inv.Expression.ToString();
            if (target.Contains(digitalBrainPrefix, StringComparison.Ordinal))
            {
                return true;
            }
            if (inv.Ancestors().OfType<BlockSyntax>().Any(b => b.ToString().Contains(digitalBrainPrefix + ".", StringComparison.Ordinal))
                && target.Contains("Get", StringComparison.Ordinal))
            {
                // Ignore CredentialBoundaryFacts reflecting its own test methods in tests
                foreach (var expr in inv.Expression.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>())
                {
                    if (expr.Type.ToString().Contains(digitalBrainPrefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // Rule #4: EnterKernel( only in Kernel.cs.
    [Fact]
    public void HouseRule04_EnterKernelClosedAllowlist()
    {
        var allowed = new HashSet<(string Project, string File)>
        {
            ("IntoChat.OS.Kernel", "Kernel.cs"),
            // The one place os platform code releases a credential to module code as the kernel.
            ("IntoChat.OS.Host", "PlatformCall.cs"),
        };
        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            if (allowed.Contains((src.ProjectName, src.FileName)))
            {
                continue;
            }
            var calls = FindEnterKernelLines(src.Root);
            foreach (var line in calls)
            {
                offenders.Add($"{DisplayPath(src)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "EnterKernel() outside closed Kernel composition allowlist:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HouseRule04_ChallengerCatchesEnterKernelElsewhere()
    {
        var sample = ParseSample(
            "class X { void M(EdgeAuthority e, BrainId b) { using var _ = e.EnterKernel(b); } }",
            TestContext.Current.CancellationToken);
        Assert.Single(FindEnterKernelLines(sample));
    }

    private static List<int> FindEnterKernelLines(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                ? ma.Name.Identifier.Text == "EnterKernel"
                : inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "EnterKernel")
            .Select(inv => inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    // Rule #5: Raw string brainId parameter ratchet.
    [Fact]
    public void HouseRule05_RawStringBrainIdParameterRatchet()
    {
        var count = 0;
        foreach (var src in ProductionSources.Value)
        {
            foreach (var param in src.Root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (param.Identifier.Text != "brainId" || param.Type is null)
                {
                    continue;
                }
                var typeName = param.Type.ToString().TrimEnd('?');
                if (typeName is "string" or "String" or "System.String")
                {
                    count++;
                }
            }
        }

        const int RatchetCap = 260;
        Assert.True(count <= RatchetCap, $"Raw `string brainId` parameters regressed to {count} (ratchet cap {RatchetCap}); use BrainId.");
    }

    // Rule #6: No /apps/, /settings, or /{alias}/default neuron-key string concatenation or splitting;
    // zero ad-hoc $"{brain}/..." key interpolations outside typed Contract/primitive helpers.
    [Fact]
    public void HouseRule06_NoAppsNeuronKeyStringSplittingOutsideAppRef()
    {
        var offenders = new List<string>();
        var settingsOffenders = new List<string>();
        var unitDefaultOffenders = new List<string>();
        var brainKeyOffenders = new List<string>();

        foreach (var src in ProductionSources.Value)
        {
            if (src.ProjectName == "IntoChat.OS.Host" && src.FileName == "AppRef.cs")
            {
                continue;
            }
            foreach (var line in FindAppsNeuronKeyStringOps(src.Root))
            {
                offenders.Add($"{DisplayPath(src)}:{line}");
            }
            // The settings contract lives in the Catalog contracts folder, whichever project scanned it first.
            if (!(src.FileName == "IUserSettings.cs" && src.FullPath.Split(Path.DirectorySeparatorChar).Contains("Contracts", StringComparer.Ordinal)))
            {
                foreach (var line in FindSettingsKeyInterpolations(src.Root))
                {
                    settingsOffenders.Add($"{DisplayPath(src)}:{line}");
                }
            }
            if (!(src.ProjectName == "IntoChat.OS.Host" && src.FileName == "UnitAlias.cs"))
            {
                foreach (var line in FindUnitDefaultKeyInterpolations(src.Root))
                {
                    unitDefaultOffenders.Add($"{DisplayPath(src)}:{line}");
                }
            }
            if (!IsTypedKeyBuilderFile(src))
            {
                foreach (var (line, expr) in FindAdHocBrainKeyInterpolations(src.Root))
                {
                    brainKeyOffenders.Add($"{DisplayPath(src)}:{line}: {expr}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Manual apps-key string operations outside AppRef.cs:\n" + string.Join('\n', offenders));
        Assert.True(settingsOffenders.Count == 0, "Manual `$\"{...}/settings\"` key strings outside IUserSettings.Key:\n" + string.Join('\n', settingsOffenders));
        Assert.True(unitDefaultOffenders.Count == 0, "Manual `$\"{...}/{...}/default\"` unit state key strings outside UnitAlias:\n" + string.Join('\n', unitDefaultOffenders));

        const int RatchetCap = 0;
        Assert.True(
            brainKeyOffenders.Count <= RatchetCap,
            $"Ad-hoc brain-key string interpolations regressed to {brainKeyOffenders.Count} (ratchet cap {RatchetCap}):\n" + string.Join('\n', brainKeyOffenders));
    }

    [Fact]
    public void HouseRule06_ChallengerCatchesBrainKeyInterpolation()
    {
        var sample = ParseSample("""
            class BadCalls
            {
                string Apps(string brainId, string name) => $"{brainId}/apps/{name}";
                string Settings(string brainId) => $"{brainId}/settings";
                string UnitDefault(string brainId, string alias) => $"{brainId}/{alias}/default";
                string AdHoc(string brainId) => $"{brainId}/circles";
                string FullKey => $"{Id.Brain.Value}/{Id.Key}";
                static string Key(string brainId) => $"{brainId}/my";
                string OkRoute(string brainId) => $"/brains/{brainId}/circles";
                string OkUri(string host, string path) => $"https://{host}/{path}";
            }
            """, TestContext.Current.CancellationToken);
        Assert.Single(FindAppsNeuronKeyStringOps(sample));
        Assert.Single(FindSettingsKeyInterpolations(sample));
        Assert.Single(FindUnitDefaultKeyInterpolations(sample));
        Assert.Equal(4, FindAdHocBrainKeyInterpolations(sample).Count);
    }

    private static bool IsTypedKeyBuilderFile(ParsedSource src)
        => src.ProjectName.EndsWith(".Contracts", StringComparison.Ordinal)
            || src.PathSegments.Contains("Contracts", StringComparer.OrdinalIgnoreCase)
            || src.PathSegments.Contains("ShellContracts", StringComparer.OrdinalIgnoreCase)
            || (src.ProjectName == "IntoChat.OS.Host" && src.FileName is "AppRef.cs" or "UnitAlias.cs")
            || (src.ProjectName == "IntoChat.OS" && src.FileName == "NeuronKeys.cs");

    private static List<int> FindSettingsKeyInterpolations(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<InterpolatedStringExpressionSyntax>()
            .Where(interp => interp.Contents.Count == 2
                && interp.Contents[0] is InterpolationSyntax
                && interp.Contents[1] is InterpolatedStringTextSyntax { TextToken.ValueText: "/settings" })
            .Select(interp => interp.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    private static List<int> FindUnitDefaultKeyInterpolations(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<InterpolatedStringExpressionSyntax>()
            .Where(interp => interp.Contents.Count == 4
                && interp.Contents[0] is InterpolationSyntax
                && interp.Contents[1] is InterpolatedStringTextSyntax { TextToken.ValueText: "/" }
                && interp.Contents[2] is InterpolationSyntax
                && interp.Contents[3] is InterpolatedStringTextSyntax { TextToken.ValueText: "/default" })
            .Select(interp => interp.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    private static List<(int Line, string Expr)> FindAdHocBrainKeyInterpolations(CompilationUnitSyntax root)
    {
        var hits = new List<(int Line, string Expr)>();
        foreach (var interp in root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
        {
            if (interp.Contents.Count < 2
                || interp.Contents[0] is not InterpolationSyntax first
                || interp.Contents[1] is not InterpolatedStringTextSyntax textAfter
                || !textAfter.TextToken.ValueText.StartsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var firstExpr = first.Expression.ToString();
            if (!Regex.IsMatch(firstExpr, @"(?<![A-Za-z0-9_])(brain|brainId|BrainId|ownerBrain|circleBrain|orgBrain|SystemBrain|CurrentBrain|Scope|scope|Brain\.Value)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant))
            {
                continue;
            }

            var raw = interp.ToString();
            if (raw.Contains("://", StringComparison.Ordinal)
                || raw.Contains(' ', StringComparison.Ordinal)
                || raw.Contains('?', StringComparison.Ordinal)
                || raw.Contains('&', StringComparison.Ordinal))
            {
                continue;
            }

            if (interp.Ancestors().OfType<TupleExpressionSyntax>().Any()
                || interp.Ancestors().OfType<ThrowStatementSyntax>().Any()
                || interp.Ancestors().OfType<ThrowExpressionSyntax>().Any())
            {
                continue;
            }

            if (interp.Ancestors().OfType<ObjectCreationExpressionSyntax>().Any(oc => ExtractSimpleTypeName(oc.Type) is "Uri" or "InvalidOperationException" or "ArgumentException" or "KeyNotFoundException" or "UnauthorizedAccessException"))
            {
                continue;
            }

            if (interp.Ancestors().OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text is "FullKey" or "OwnerKey")
                || interp.Ancestors().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text is "Key" or "Of" or "FullKey"))
            {
                continue;
            }

            hits.Add((interp.GetLocation().GetLineSpan().StartLinePosition.Line + 1, raw));
        }
        return hits;
    }

    private static List<int> FindAppsNeuronKeyStringOps(CompilationUnitSyntax root)
    {
        var lines = new List<int>();
        foreach (var interp in root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
        {
            var text = interp.ToString();
            if (!text.Contains("/apps/", StringComparison.Ordinal))
            {
                continue;
            }
            if (text.Contains("neuron://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("$\"/brains/", StringComparison.Ordinal)
                || text.StartsWith("$\"/apps/", StringComparison.Ordinal)
                || text.StartsWith("$\"/api/", StringComparison.Ordinal))
            {
                continue;
            }
            lines.Add(interp.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text is "Split" or "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf"
                && inv.ArgumentList.Arguments.Any(a => a.Expression is LiteralExpressionSyntax lit
                    && lit.Token.ValueText is "/apps/" or "/apps"))
            {
                var exprText = ma.Expression.ToString();
                if (exprText.Contains("Path", StringComparison.OrdinalIgnoreCase)
                    || exprText.Contains("Url", StringComparison.OrdinalIgnoreCase)
                    || exprText.Contains("Uri", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                lines.Add(inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }
        return lines;
    }

    // Rule #7: Zero new BrainId("local") in production source.
    [Fact]
    public void HouseRule07_NoLocalBrainIdLiteralInProduction()
    {
        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            foreach (var line in FindLocalBrainIdConstructions(src.Root))
            {
                offenders.Add($"{DisplayPath(src)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "Forbidden new BrainId(\"local\") in production source:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HouseRule07_ChallengerCatchesLocalBrainId()
    {
        var sample = ParseSample(
            "class X { object M() => new BrainId(\"local\"); }",
            TestContext.Current.CancellationToken);
        Assert.Single(FindLocalBrainIdConstructions(sample));
    }

    private static List<int> FindLocalBrainIdConstructions(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(oc => (oc.Type.ToString() is "BrainId" or "DigitalBrain.BrainId")
                && oc.ArgumentList is { Arguments.Count: 1 }
                && oc.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit
                && lit.Token.ValueText == "local")
            .Select(oc => oc.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    // Rule #8: No manual "." / ".." segment checks outside SafeRelativePath.cs.
    [Fact]
    public void HouseRule08_NoManualDotSegmentChecksOutsideSafeRelativePath()
    {
        // Closed set of non-OS-filesystem single-segment identifier/blob/unit-token guards:
        var nonRelativePathTokenGuards = new HashSet<(string Project, string File)>
        {
            ("IntoChat.OS.Host", "AppRef.cs"),
            ("IntoChat.OS.Host", "SafeRelativePath.cs"),
            ("IntoChat.OS.Modules.Catalog", "SlotNamesABrainNeuron.cs"),
            ("IntoChat.OS.Modules.Roslyn", "CodeGraphService.cs"),
            ("IntoChat.OS.Modules.Shell", "ViewportNeuron.cs"),
            ("IntoChat.OS.Modules.Shell", "WorkspaceNeuron.cs"),
            ("IntoChat.OS.Modules.Windows", "WindowsStorageWrites.cs"),
        };
        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            if (nonRelativePathTokenGuards.Contains((src.ProjectName, src.FileName)))
            {
                continue;
            }
            foreach (var line in FindDotSegmentChecks(src.Root))
            {
                offenders.Add($"{DisplayPath(src)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "Manual dot-segment path checks outside SafeRelativePath.cs:\n" + string.Join('\n', offenders));
    }

    private static List<int> FindDotSegmentChecks(CompilationUnitSyntax root)
    {
        var lines = new List<int>();
        foreach (var pat in root.DescendantNodes().OfType<ConstantPatternSyntax>())
        {
            if (IsDotLit(pat.Expression))
            {
                lines.Add(pat.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }
        foreach (var bin in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (bin.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
                && (IsDotLit(bin.Left) || IsDotLit(bin.Right)))
            {
                lines.Add(bin.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }
        return lines;

        static bool IsDotLit(ExpressionSyntax expr) =>
            expr is LiteralExpressionSyntax l
            && l.IsKind(SyntaxKind.StringLiteralExpression)
            && l.Token.ValueText is "." or "..";
    }

    // Rule #9: No bool constructor parameters on Rule/Rules types or Decision.Rule/Require bool overloads.
    [Fact]
    public void HouseRule09_NoBoolParamsOnRulesOrDecision()
    {
        var badRules = new List<string>();
        foreach (var type in ProductionArchitecture.Types)
        {
            if (!(type.Name.EndsWith("Rule", StringComparison.Ordinal) || type.Name.EndsWith("Rules", StringComparison.Ordinal)))
            {
                continue;
            }
            if (type.IsNestedPrivate)
            {
                continue;
            }
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (ctor.GetParameters().Any(p => p.ParameterType == typeof(bool)))
                {
                    badRules.Add($"{type.FullName}({string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                }
            }
        }

        var badDecision = typeof(Decision).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name is "Rule" or "Require" && m.GetParameters().Any(p => p.ParameterType == typeof(bool)))
            .Select(m => m.ToString()!)
            .ToList();

        Assert.True(badRules.Count == 0, "Rule types with bool ctor parameters:\n" + string.Join('\n', badRules));
        Assert.True(badDecision.Count == 0, "Decision.Rule/Require bool overloads still present:\n" + string.Join('\n', badDecision));
    }

    // Rule #10: All domain primitives under Framework/Primitives/ expose private constructors only.
    [Fact]
    public void HouseRule10_DomainPrimitivesExposePrivateConstructorsOnly()
    {
        var primitiveSources = new[]
        {
            (Dir: Path.Combine(ProjectDirs.Value["IntoChat.OS.Host"], "Primitives"), typeof(AppRef).Assembly, Pattern: "*.cs"),
            (Dir: ProjectDirs.Value["IntoChat.OS"], typeof(ConversationId).Assembly, Pattern: "ConversationId.cs"),
        };
        var discoveredTypes = new List<Type>();
        var bad = new List<string>();

        foreach (var (primitivesDir, primitiveAssembly, pattern) in primitiveSources)
        {
            foreach (var file in Repository.SourceFiles(primitivesDir, pattern))
            {
                var root = ParseSample(File.ReadAllText(file), TestContext.Current.CancellationToken);
                foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (decl.Modifiers.Any(SyntaxKind.StaticKeyword))
                    {
                        continue;
                    }
                    var ns = decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                    if (ns is null)
                    {
                        continue;
                    }
                    var fullName = $"{ns}.{decl.Identifier.Text}";
                    var runtimeType = primitiveAssembly.GetType(fullName);
                    Assert.True(runtimeType is not null, $"Could not load primitive type '{fullName}' from {primitiveAssembly.GetName().Name}.");
                    discoveredTypes.Add(runtimeType!);

                    foreach (var badDeclLine in FindNonPrivateCtorDeclarations(decl))
                    {
                        bad.Add($"{Repository.Relative(file)}:{badDeclLine}: {decl.Identifier.Text} declares non-private constructor syntax");
                    }
                    foreach (var ctor in runtimeType!.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (!ctor.IsPrivate)
                        {
                            bad.Add($"{runtimeType.Name}({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))}) visibility={(ctor.IsPublic ? "public" : ctor.IsAssembly ? "internal" : "non-private")}");
                        }
                    }
                }
            }
        }

        Assert.True(discoveredTypes.Count >= 4, $"Expected at least 4 domain primitives; found {discoveredTypes.Count}.");
        Assert.True(bad.Count == 0, "Domain primitive constructors must be strictly private:\n" + string.Join('\n', bad));
    }

    [Fact]
    public void HouseRule10_ChallengerCatchesPublicPrimitiveConstructor()
    {
        var sample = ParseSample("""
            public readonly record struct BadRecord(string Value);
            public readonly struct BadStruct { public BadStruct(string value) {} }
            public readonly record struct GoodPrimitive { private GoodPrimitive(string value) {} }
            """, TestContext.Current.CancellationToken);
        var decls = sample.DescendantNodes().OfType<TypeDeclarationSyntax>().ToArray();
        Assert.Single(FindNonPrivateCtorDeclarations(decls[0]));
        Assert.Single(FindNonPrivateCtorDeclarations(decls[1]));
        Assert.Empty(FindNonPrivateCtorDeclarations(decls[2]));
    }

    private static List<int> FindNonPrivateCtorDeclarations(TypeDeclarationSyntax typeDecl)
    {
        var lines = new List<int>();
        if (typeDecl.ParameterList is not null && !typeDecl.Modifiers.Any(SyntaxKind.PrivateKeyword))
        {
            lines.Add(typeDecl.ParameterList.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
        foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (ctor.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                continue;
            }
            if (!ctor.Modifiers.Any(SyntaxKind.PrivateKeyword))
            {
                lines.Add(ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }
        return lines;
    }

    // Rule #11: Zero in-place state field/property mutations in [Neuron] / Screen classes.
    [Fact]
    public void HouseRule11_NoInPlaceStateMutationInNeuronsOrScreens()
    {
        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            foreach (var cls in src.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(IsNeuronOrScreenClass))
            {
                foreach (var line in FindInPlaceStateMutations(cls))
                {
                    offenders.Add($"{DisplayPath(src)}:{line} ({cls.Identifier.Text})");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Direct state mutation inside Neuron/Screen (use PersistState / SetState):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HouseRule11_ChallengerCatchesStateMutation()
    {
        var sample = ParseSample("""
            [Neuron("x")] class X : Neuron<S> { void M() { CurrentState.Count = 1; } }
            """, TestContext.Current.CancellationToken);
        var cls = sample.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        Assert.Single(FindInPlaceStateMutations(cls));
    }

    private static List<int> FindInPlaceStateMutations(ClassDeclarationSyntax cls) =>
        [.. cls.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax ma
                && IsStateTarget(ma.Expression)
                && !ma.Ancestors().OfType<AnonymousObjectCreationExpressionSyntax>().Any()
                && ma.Ancestors().OfType<InitializerExpressionSyntax>().All(init => init.Parent is not ObjectCreationExpressionSyntax oc || !oc.Span.Contains(a.Span)))
            .Select(a => a.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    private static bool IsStateTarget(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text is "State" or "CurrentState" or "Snapshot" or "ActiveState",
        MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text is "State" or "CurrentState" or "Snapshot" or "ActiveState",
        _ => false,
    };

    // Rule #12: TimeProvider in Neurons & Screens.
    [Fact]
    public void HouseRule12_NoWallClockOrUnexpectedTimeProviderSystemInNeuronsOrScreens()
    {
        var detachedFallbackAllowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            "InoApp",
            "FriendsApp",
            "CreatorApp",
            "AggregateScreenBase",
            "IdentityDirectoryNeuron",
            "LocalCloudNeuron",
        };

        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            foreach (var cls in src.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(IsNeuronOrScreenClass))
            {
                var (wallLines, systemLines) = FindClockViolations(cls);
                foreach (var line in wallLines)
                {
                    offenders.Add($"{DisplayPath(src)}:{line} ({cls.Identifier.Text}) uses wall clock DateTime*.Now/UtcNow");
                }
                if (!detachedFallbackAllowlist.Contains(cls.Identifier.Text))
                {
                    foreach (var line in systemLines)
                    {
                        offenders.Add($"{DisplayPath(src)}:{line} ({cls.Identifier.Text}) uses TimeProvider.System instead of Context.Time / Time");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Clock violations in Neuron/Screen classes:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HouseRule12_ChallengerCatchesWallClockAndTimeProviderSystem()
    {
        var sample = ParseSample("""
            [Neuron("x")] class X : Neuron<S> { void M() { _ = DateTimeOffset.UtcNow; _ = TimeProvider.System; } }
            """, TestContext.Current.CancellationToken);
        var cls = sample.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        var (wall, sys) = FindClockViolations(cls);
        Assert.Single(wall);
        Assert.Single(sys);
    }

    private static (List<int> Wall, List<int> System) FindClockViolations(ClassDeclarationSyntax cls)
    {
        var wall = new List<int>();
        var sys = new List<int>();
        foreach (var ma in cls.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var left = ma.Expression.ToString();
            var right = ma.Name.Identifier.Text;
            if ((left is "DateTime" or "DateTimeOffset" or "System.DateTime" or "System.DateTimeOffset")
                && right is "Now" or "UtcNow")
            {
                wall.Add(ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
            else if (left is "TimeProvider" or "System.TimeProvider" && right == "System")
            {
                sys.Add(ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }
        return (wall, sys);
    }

    // Rule #13: Zero direct new <ConcreteNeuronOrScreen>() in production or tests.
    [Fact]
    public void HouseRule13_NoDirectInstantiationOfConcreteNeuronsOrScreens()
    {
        var concreteTargets = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
        foreach (var t in ProductionArchitecture.Types)
        {
            if (t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition)
            {
                continue;
            }
            if (!IsNeuronOrScreenRuntimeType(t))
            {
                continue;
            }
            var simpleName = t.Name;
            var tick = simpleName.IndexOf('`');
            if (tick >= 0)
            {
                simpleName = simpleName[..tick];
            }
            if (!concreteTargets.TryGetValue(simpleName, out var list))
            {
                list = [];
                concreteTargets[simpleName] = list;
            }
            list.Add(t);
        }

        var offenders = new List<string>();
        foreach (var src in AllSourcesAndTests.Value)
        {
            foreach (var oc in src.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var typeName = ExtractSimpleTypeName(oc.Type);
                if (typeName is null || !concreteTargets.TryGetValue(typeName, out var matchedTypes))
                {
                    continue;
                }
                if (!MightReferToAny(src.Root, matchedTypes))
                {
                    continue;
                }
                var line = oc.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add($"{DisplayPath(src)}:{line}: new {oc.Type}(...)");
            }
        }

        Assert.True(offenders.Count == 0, "Direct `new <Neuron/Screen>(...)` instantiation forbidden (use TestOS / AggregateExplorer / ScreenPreview / DI):\n" + string.Join('\n', offenders));
    }

    private static bool IsNeuronOrScreenRuntimeType(Type t)
    {
        for (var cur = t; cur is not null; cur = cur.BaseType)
        {
            if (cur.FullName?.StartsWith("DigitalBrain.Neurons.Neuron", StringComparison.Ordinal) == true
                || cur.FullName?.StartsWith("DigitalBrain.Screen", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }
        return t.CustomAttributes.Any(a => a.AttributeType.FullName == "DigitalBrain.NeuronAttribute");
    }

    private static string? ExtractSimpleTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax gen => gen.Identifier.Text,
        QualifiedNameSyntax q => ExtractSimpleTypeName(q.Right),
        AliasQualifiedNameSyntax aq => ExtractSimpleTypeName(aq.Name),
        _ => null,
    };

    private static bool MightReferToAny(CompilationUnitSyntax root, List<Type> types)
    {
        var usings = root.Usings.Select(u => u.Name?.ToString()).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        return types.Any(t =>
            t.Namespace is { } ns && (usings.Contains(ns) || namespaces.Any(localNs => localNs == ns || localNs.StartsWith(ns + ".", StringComparison.Ordinal) || ns.StartsWith(localNs + ".", StringComparison.Ordinal))));
    }

    // Rule #14: Zero Postgres table slot/hook registrations in Kernel.
    [Fact]
    public void HouseRule14_KernelHasNoPostgresSlotOrHookRegistrations()
    {
        var kernelDir = ProjectDirs.Value["IntoChat.OS.Kernel"];
        string[] banned = ["ITablesSlot", "IAppStorageHook", "AppTablesSlot", "TableStorageHook"];
        var offenders = new List<string>();
        foreach (var file in Repository.SourceFiles(kernelDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var token in banned)
            {
                if (Regex.IsMatch(text, $@"\b{token}\b", RegexOptions.CultureInvariant))
                {
                    offenders.Add($"{Repository.Relative(file)}: mentions {token}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Kernel must not reference Postgres table slots/hooks:\n" + string.Join('\n', offenders));
    }

    // Rule #15: Zero non-test InternalsVisibleTo.
    [Fact]
    public void HouseRule15_ZeroNonTestInternalsVisibleTo()
    {
        var checks = typeof(InternalsVisibleToFacts).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(m => m.IsDefined(typeof(FactAttribute), false) || m.IsDefined(typeof(TheoryAttribute), false));
        Assert.True(checks >= 1, "InternalsVisibleToFacts must remain active.");

        var csprojs = ProjectDirs.Value
            .Where(p => !p.Key.Contains(".Tests", StringComparison.Ordinal))
            .Select(p => p.Value)
            .SelectMany(dir => Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(dir, "Directory.Build.props", SearchOption.AllDirectories)))
            .Concat(Repository.ModuleRoots.Select(root => Path.Combine(root, "Directory.Build.props")))
            .Append(Path.Combine(Repository.Root, "Directory.Build.props"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();
        foreach (var file in csprojs)
        {
            if (!File.Exists(file))
            {
                continue;
            }
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<InternalsVisibleTo\s+Include=""([^""]+)""", RegexOptions.CultureInvariant))
            {
                var grantee = m.Groups[1].Value;
                if (!grantee.Contains(".Tests", StringComparison.Ordinal))
                {
                    offenders.Add($"{Repository.Relative(file)}: {grantee}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Non-test InternalsVisibleTo entries forbidden in csproj/props:\n" + string.Join('\n', offenders));
    }

    // Rule #16: Public types in Modules outside Contracts/ must be [Module], *Options, or closed boundary allowlist.
    [Fact]
    public void HouseRule16_ModulesExposeOnlyContractsAndClosedAllowlistPublicly()
    {
        var closedAllowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            // Module host/config enums & key constants (not contract payloads)
            "IntoChat.OS.Modules.AI.AIKeys",
            "IntoChat.OS.Modules.AI.AIOptionsExtensions",
            "IntoChat.OS.Modules.Postgres.PostgresIsolation",
            "IntoChat.OS.Modules.Flutter.FlutterHostKind",
            // Cross-assembly app/kernel/unit composition or Orleans-activated grains
            "IntoChat.OS.Modules.AI.Agents.AgentFailures",
            "IntoChat.OS.Modules.AI.Models.ModelCatalog",
            "IntoChat.OS.Modules.Devices.OllamaRuntime",
            // A host registers the workflows it offers; it carries an Agent Framework factory, so it is not a contract.
            "IntoChat.OS.Modules.AI.Workflows.WorkflowDefinition",
            "IntoChat.OS.Modules.Microsoft.Azure.ArmAzure",
            "IntoChat.Aspire.CandidateEnvironment",
            "IntoChat.OS.Modules.Apps.OperationReceipt",
            "IntoChat.OS.Modules.Apps.Approvals.ApprovalsNeuron",
            "IntoChat.OS.Modules.Apps.AppState",
            "IntoChat.OS.Modules.Apps.AppVersionRecord",
            "IntoChat.OS.Modules.Apps.StringEntry",
            "IntoChat.OS.Modules.Apps.AppReleaseRecord",
            "IntoChat.OS.Modules.Apps.DeadLetterEntry",
            "IntoChat.OS.Modules.Apps.RetainedRequest",
            "IntoChat.OS.Modules.Apps.BrainGraph",
            "IntoChat.OS.Modules.Apps.Debugger.ScenarioTranscripts",
            "IntoChat.OS.Modules.Apps.CompiledAppsRegistration",
            "IntoChat.OS.Modules.Apps.FeatureCatalog",
            "IntoChat.OS.Modules.Apps.FeatureLoader",
            "IntoChat.OS.Modules.Apps.Features.Module",
            "IntoChat.OS.Modules.Apps.ShippedInstall",
            "IntoChat.OS.Modules.Apps.IFilesVfsService",
            "IntoChat.OS.Modules.Apps.IWithFilesVfs",
            "IntoChat.OS.Modules.Apps.WorkspaceFilesService",
            "IntoChat.OS.Modules.Apps.Flows.AgentInner",
            "IntoChat.OS.Modules.Apps.Flows.AgentFlow`1",
            "IntoChat.OS.Modules.Apps.Flows.BuildInner",
            "IntoChat.OS.Modules.Apps.Flows.BuildFlow",
            "IntoChat.OS.Modules.Apps.Flows.PlanRelation",
            "IntoChat.OS.Modules.Apps.Flows.PlanSubtask",
            "IntoChat.OS.Modules.Apps.Flows.PlanInner",
            "IntoChat.OS.Modules.Apps.Flows.PlanFlow",
            "IntoChat.OS.Modules.Apps.ArtCraftEngineRunner",
            "IntoChat.OS.Modules.Apps.DataAnalyticsEngine",
            "IntoChat.OS.Modules.Apps.InoDataCards",
            "IntoChat.OS.Modules.Apps.AppInstaller",
            "IntoChat.OS.Modules.Apps.MarketplaceView",
            "IntoChat.OS.Modules.Apps.PortableBrainSnapshot",
            "IntoChat.OS.Modules.Apps.PortableBrainArchive",
            "IntoChat.OS.Modules.Apps.ProfileNeuron",
            "IntoChat.OS.Modules.Apps.UserSettingsNeuron",
            "IntoChat.OS.Modules.Microsoft.GitHub.WorkTree.WorkTreeService",
            "IntoChat.OS.Modules.Tools.ToolCapabilitySource",
            "IntoChat.OS.Modules.Flutter.Settings.Choice",
            "IntoChat.OS.Modules.LocalDisk.LocalCloudState",
            "IntoChat.OS.Modules.LocalDisk.LocalCloudEntry",
            "IntoChat.OS.Modules.LocalDisk.LocalCloudNeuron",
            "IntoChat.OS.Modules.LocalDisk.LocalCloudStorage",
        };

        var contractsDeclaredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in ProductionSources.Value)
        {
            if (!src.ProjectName.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal)
                || (!src.ProjectName.EndsWith(".Contracts", StringComparison.Ordinal)
                    && !src.PathSegments.Contains("Contracts", StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            foreach (var decl in src.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var ns = decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                if (ns is null)
                {
                    continue;
                }
                var full = $"{ns}.{decl.Identifier.Text}";
                if (decl is TypeDeclarationSyntax td && td.TypeParameterList is { Parameters.Count: > 0 } tp)
                {
                    full = $"{full}`{tp.Parameters.Count}";
                }
                contractsDeclaredNames.Add(full);
            }
        }

        var moduleAssemblies = ProductionArchitecture.Assemblies
            .Where(a => a.GetName().Name?.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal) == true
                && a.GetName().Name != "IntoChat.OS.Modules.Identity"
                // IntoChat.OS.Modules.UI uses vertical-slice co-located contracts (enforced by UiVerticalSliceFacts).
                && a.GetName().Name != "IntoChat.OS.Modules.UI")
            .ToArray();

        var unexpected = new List<string>();
        foreach (var asm in moduleAssemblies)
        {
            foreach (var t in asm.GetTypes())
            {
                if (!t.IsPublic || t.Namespace is null
                    || t.Namespace.StartsWith("OrleansCodeGen.", StringComparison.Ordinal))
                {
                    continue;
                }
                if (contractsDeclaredNames.Contains(t.FullName!))
                {
                    continue;
                }
                if (typeof(DigitalBrain.Modules.Module).IsAssignableFrom(t)
                    || t.GetInterfaces().Any(i => i.Name == "IModuleOptions")
                    || t.Name.EndsWith("Options", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!closedAllowlist.Contains(t.FullName!))
                {
                    unexpected.Add($"{asm.GetName().Name}: {t.FullName}");
                }
            }
        }

        Assert.True(unexpected.Count == 0, "Unexpected public type in Modules outside Contracts/ (make internal or move to Contracts/):\n" + string.Join('\n', unexpected));
    }

    // Rule #17: Folder = namespace strictly across non-Module core assemblies (complementing NamespaceSchemeFacts).
    [Fact]
    public void HouseRule17_FolderMatchesNamespaceInNonModuleAssemblies()
    {
        string[] targetProjects =
        [
            "IntoChat.OS.LocalSetup",
            "IntoChat.OS.UI",
            "IntoChat.OS.Sandbox",
            "IntoChat.OS.Desktop",
            "IntoChat.OS.Apps",
            "IntoChat.OS.Kernel",
        ];

        // KernelGrains.cs holds persisted/grain wire types whose namespace is pinned in ContractManifest.json.
        var manifest = ContractManifest.Load(ContractManifest.BaselinePath);
        Assert.Contains(manifest.Grains.Keys, k => k.StartsWith("IntoChat.OS.Kernel.Composition.", StringComparison.Ordinal));

        var mismatches = new List<string>();
        foreach (var projectName in targetProjects)
        {
            var dir = ProjectDirs.Value[projectName];
            foreach (var file in Repository.SourceFiles(dir, "*.cs"))
            {
                var segments = Path.GetRelativePath(dir, Path.GetDirectoryName(file)!).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (projectName == "IntoChat.OS.Apps" && segments.Contains("Units", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (projectName == "IntoChat.OS.Kernel"
                    && segments.SequenceEqual(["Composition", "Kernel"])
                    && string.Equals(Path.GetFileName(file), "KernelGrains.cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var expectedPrefix = segments.Length == 1 && segments[0] == "."
                    ? projectName
                    : $"{projectName}.{string.Join('.', segments)}";
                var root = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(file),
                    cancellationToken: TestContext.Current.CancellationToken).GetCompilationUnitRoot(TestContext.Current.CancellationToken);
                foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                {
                    var actual = ns.Name.ToString();
                    if (actual != expectedPrefix && !actual.StartsWith(expectedPrefix + ".", StringComparison.Ordinal))
                    {
                        mismatches.Add($"{Repository.Relative(file)}: declared '{actual}', expected '{expectedPrefix}'");
                    }
                }
            }
        }

        Assert.True(mismatches.Count == 0, "Namespace != folder in core assemblies:\n" + string.Join('\n', mismatches));
    }

    // Rule #18: *Service classes own zero Dictionary/Regex/Rule fields/properties or [GeneratedRegex].
    [Fact]
    public void HouseRule18_ServiceClassesOwnZeroPolicyStateDictionariesOrRegex()
    {
        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            foreach (var cls in src.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.Identifier.Text.EndsWith("Service", StringComparison.Ordinal)))
            {
                foreach (var violation in FindServiceStateViolations(cls))
                {
                    offenders.Add($"{DisplayPath(src)}: {cls.Identifier.Text} -> {violation}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "*Service classes must stay thin dispatchers (no Dictionary/Regex/Rule fields/properties):\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HouseRule18_ChallengerCatchesServiceDictionaryAndRegex()
    {
        var sample = ParseSample("""
            partial class FooService
            {
                private readonly Dictionary<string, int> _cache = new();
                [GeneratedRegex("x")] private static partial Regex Bad();
            }
            """, TestContext.Current.CancellationToken);
        var cls = sample.DescendantNodes().OfType<ClassDeclarationSyntax>().Single();
        Assert.Equal(2, FindServiceStateViolations(cls).Count);
    }

    private static List<string> FindServiceStateViolations(ClassDeclarationSyntax cls)
    {
        var violations = new List<string>();
        foreach (var field in cls.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            var typeStr = field.Declaration.Type.ToString();
            if (IsForbiddenServiceType(typeStr))
            {
                violations.Add($"field {typeStr}");
            }
        }
        foreach (var prop in cls.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var typeStr = prop.Type.ToString();
            if (IsForbiddenServiceType(typeStr))
            {
                violations.Add($"property {typeStr} {prop.Identifier.Text}");
            }
        }
        foreach (var attr in cls.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (attr.Name.ToString().Contains("GeneratedRegex", StringComparison.Ordinal))
            {
                violations.Add("[GeneratedRegex]");
            }
        }
        return violations;

        static bool IsForbiddenServiceType(string typeStr) =>
            Regex.IsMatch(typeStr, @"\b(Dictionary|ConcurrentDictionary|IReadOnlyDictionary|Regex|\w*Rule)\b", RegexOptions.CultureInvariant);
    }

    // Rule #19: Zero implicit operator string in production source.
    [Fact]
    public void HouseRule19_ZeroImplicitStringOperators()
    {
        var offenders = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            foreach (var line in FindImplicitStringOperatorLines(src.Root))
            {
                offenders.Add($"{DisplayPath(src)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0, "Forbidden `implicit operator string` in production source:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void HouseRule19_ChallengerCatchesImplicitStringOperator()
    {
        var sample = ParseSample(
            "readonly struct Id { public static implicit operator string(Id id) => \"\"; }",
            TestContext.Current.CancellationToken);
        Assert.Single(FindImplicitStringOperatorLines(sample));
    }

    private static List<int> FindImplicitStringOperatorLines(CompilationUnitSyntax root) =>
        [.. root.DescendantNodes()
            .OfType<ConversionOperatorDeclarationSyntax>()
            .Where(op => op.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword)
                && op.Type.ToString() is "string" or "String" or "System.String")
            .Select(op => op.GetLocation().GetLineSpan().StartLinePosition.Line + 1)];

    // Rule #20: Non-Contracts/ interfaces with single consumer belong to an explicit justified table.
    [Fact]
    public void HouseRule20_NonContractsInterfacesFollowJustifiedTable()
    {
        var justifiedNonContractInterfaces = new HashSet<string>(StringComparer.Ordinal)
        {
            // LocalSetup & OS/process/FS/HTTP host boundary test seams
            "IPreflightCheck",
            "IProbeEnvironment",
            "IResetFileSystem",
            "IDockerCli",
            "IConfinedProcessRunner",
            "ILicenseCheck",
            "IPresenceProof",
            "IKeyWrapper",
            // Framework/Kernel runtime core abstractions (not module contracts)
            "IKernel",
            "IHostedAppTypes",
            "IModuleTypeSource",
            "ITopologyModule",
            "IRegistryBroker",
            "IRuntimeBuilder",
            "INeuronState",
            "INeuronStateStore",
            "ILogic",
            "IConnectionHosts",
            "IHttpConnection",
            "IIntoChatOSModuleHosting",
            "IRevisioned",
            "IBrain",
            "ISandboxBrain",
            "IFolder",
            "IAi",
            "ITableHandle",
            "ITables",
            "INotifications",
            "IIntoChatOSBrains",
            "ICreationHandler",
            "IBrainMembership",
            "IClusterMembershipFeed",
            "ICluster",
            "IClusterMembershipReceiver",
            "IConnectionBindings",
            "IBrainEntries",
            "IBrainProviderKeys",
            "IIdentityClient",
            "IIdentityDirectory",
            "ISecretRedactor",
            "ISecretValueSource",
            "IIntegration",
            "ICallFilterStage",
            "IBrainOwnershipCounters",
            "IBrainBirths",
            "IHandleDirectory",
            "IEdgeGrantsCache",
            "IGrantsNeuron",
            // Module-internal external SDK / OS / hardware / Orleans grain facets
            "IVolumeSource",
            "IWindowsFolderSignals",
            "IVolumeProbe",
            "ISalesforceProvider",
            "IAudioConverter",
            "IVoiceClients",
            "IFoundryLocalApi",
            "ISherpaApi",
            "ISherpaTts",
            "ISherpaVad",
            "IGitHubUserAccess",
            "IGitHubInstallationVerifier",
            "IGitHubRepositorySource",
            "IGitHubInbound",
            "IGitHubRepositoryIndex",
            "IProblemReports",
            "IPolicyModel",
            "IWebBrowserConnector",
            "ISandboxSignalRouter",
            "IGmailApi",
            "IGmailInbound",
            "IGmailPushAuthenticator",
            "ISolutionLoader",
            "ISearchAdapter",
            "ICreatorModel",
            "IClickHouseProvider",
            "ILlmProviderFactory",
            "ILocalProcessProbe",
            "IHardwareProbe",
            "IModelTurnRunner",
            "IToolRouter",
            "IMemoryEmbedder",
            "IFilesVfsService",
            "IWithFilesVfs",
            "ITypeTable",
            "IConditionallyAvailable",
            "IAiCredentials",
            "IAgentDriver",
            "IConversationDriver",
            "ITurnSink",
            "IAppRestore",
            "IPostgresProvider",
            "IConnectionPostgresProviderFactory",
            "IClickHouseSourceFactory",
            "ICandidateVerifier",
            "ITablePlacements",
            "ITablesSlot",
            "ITableIndex",
            "ITableLifetime",
            "ITableDescriptor",
            "ITableStore",
            "IPostgresTableHost",
            "IConnectionTableStores",
            "IPostgresSourceRegistry",
            "IDeploymentDriver",
            "IClusterHost",
            "IProcessRunner",
            "ICandidateHost",
            "IAspireRuntimeBridge",
            "IGrantPolicySource",
            "IOsVault",
            "IDirectoryStore",
            "IPrincipalCredential",
            "IExternalTicketReplay",
            // App-internal UI seams & IntoChat.OS.UI root navigation lifecycle contracts
            "IGalleryLibrary",
            "IWidgetbook",
            "ISecretVault",
            "INavigatedTo",
            "INavigatedFrom",
        };

        var unexpected = new List<string>();
        foreach (var src in ProductionSources.Value)
        {
            if (src.ProjectName.EndsWith(".Contracts", StringComparison.Ordinal)
                || src.ProjectName == "IntoChat.OS.Modules.UI"
                || src.PathSegments.Contains("Contracts", StringComparer.OrdinalIgnoreCase)
                || src.PathSegments.Contains("ShellContracts", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var iface in src.Root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                var name = iface.Identifier.Text;
                if (!justifiedNonContractInterfaces.Contains(name))
                {
                    unexpected.Add($"{DisplayPath(src)}: interface {name}");
                }
            }
        }

        Assert.True(unexpected.Count == 0, "Unjustified interface outside Contracts/:\n" + string.Join('\n', unexpected));
    }

    // Rule #21: Zero RegExp, .split(, .toLowerCase(, string ==/!= literal, or string switch arms in Dart nodes/ & chat/.
    [Fact]
    public void HouseRule21_DartNodesAndChatHaveZeroStringSplittingOrRegex()
    {
        var offending = new List<string>();
        foreach (var subdir in new[] { "nodes", "chat" })
        {
            var dir = Path.Combine(Repository.Root, "ui", "lib", "s" + "rc", subdir);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(dir, "*.dart", SearchOption.AllDirectories))
            {
                var rel = Repository.Relative(file).Replace('\\', '/');
                offending.AddRange(FindDartPolicyStringViolations(rel, File.ReadAllLines(file)));
            }
        }

        Assert.True(offending.Count == 0, "Dart nodes/chat must render structured models cleanly (no RegExp/split/toLowerCase/string-switch/string-eq):\n" + string.Join('\n', offending));
    }

    [Fact]
    public void HouseRule21_ChallengerCatchesAllFiveDartViolations()
    {
        var hits = FindDartPolicyStringViolations("sample.dart", [
            "final re = RegExp(r'^foo');",
            "final parts = label.split('/');",
            "final lower = role.toLowerCase();",
            "if (kind == 'error') return true;",
            "  'done' => Colors.green,",
            "  case 'pending': break;",
        ]);
        Assert.Equal(6, hits.Count);
    }

    private static readonly Regex DartStringLiteralEq = new(@"(?:==|!=)\s*['""][^'""]*['""]|['""][^'""]*['""]\s*(?:==|!=)", RegexOptions.CultureInvariant);
    private static readonly Regex DartStringSwitchArm = new(@"(?:^\s*['""][^'""]*['""]\s*=>|\bcase\s*['""][^'""]*['""]\s*:)", RegexOptions.CultureInvariant);

    private static List<string> FindDartPolicyStringViolations(string rel, string[] lines)
    {
        var offending = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("RegExp(", StringComparison.Ordinal)
                || line.Contains(".split(", StringComparison.Ordinal)
                || line.Contains(".toLowerCase(", StringComparison.Ordinal)
                || DartStringLiteralEq.IsMatch(line)
                || DartStringSwitchArm.IsMatch(line))
            {
                offending.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }
        return offending;
    }

    // Rule #22: Zero test retries or silent OS-guard skips.
    [Fact]
    public void HouseRule22_NoTestRetriesOrSilentSkips()
    {
        var checks = typeof(NoRetryFacts).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Count(m => m.IsDefined(typeof(FactAttribute), false) || m.IsDefined(typeof(TheoryAttribute), false));
        Assert.True(checks >= 3, $"Expected NoRetryFacts to enforce zero retries/skips; found {checks} test methods.");
    }

    // Rule #23: Ambient-reach ratchet in Apps and Modules (use declared Slots.Get<T> instead of ambient Get<T>/TryGetOptional<T>/Implements<T>/SiloNeurons.Get<T>).
    [Fact]
    public void HouseRule23_AmbientReachRatchet()
    {
        var appsCount = 0;
        var modulesCount = 0;
        var appsByFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var modulesByProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in ProductionSources.Value)
        {
            if (src.ProjectName.EndsWith(".Contracts", StringComparison.Ordinal)
                || src.PathSegments.Contains("Contracts", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (src.ProjectName == "IntoChat.OS.Apps")
            {
                var hits = CountAmbientReach(src.Tree);
                if (hits > 0)
                {
                    appsCount += hits;
                    var appGroup = src.PathSegments.Length > 1 ? src.PathSegments[0] : src.FileName;
                    appsByFolder[appGroup] = appsByFolder.GetValueOrDefault(appGroup) + hits;
                }
            }
            else if (IsModuleCode(src))
            {
                var hits = CountAmbientReach(src.Tree);
                if (hits > 0)
                {
                    modulesCount += hits;
                    modulesByProject[src.ProjectName] = modulesByProject.GetValueOrDefault(src.ProjectName) + hits;
                }
            }
        }

        const int AppsAmbientReachCap = 0;
        const int ModulesAmbientReachCap = 206;

        Assert.True(
            appsCount <= AppsAmbientReachCap,
            $"Ambient reach calls in Apps regressed to {appsCount} (ratchet cap {AppsAmbientReachCap}); declare slots and use Slots.Get<T>(...):\n"
                + string.Join('\n', appsByFolder.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"  {kv.Key}: {kv.Value}")));
        Assert.True(
            modulesCount <= ModulesAmbientReachCap,
            $"Ambient reach calls in Modules regressed to {modulesCount} (ratchet cap {ModulesAmbientReachCap}); declare slots and use Slots.Get<T>(...):\n"
                + string.Join('\n', modulesByProject.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"  {kv.Key}: {kv.Value}")));
    }

    [Fact]
    public void HouseRule23_AmbientReachChallengerDetectsUndeclaredGetAndIgnoresSlotsGet()
    {
        var sample = CSharpSyntaxTree.ParseText("""
            class SampleApp
            {
                void M(IServiceProvider services)
                {
                    _ = Get<ITable>("x");
                    _ = TryGetOptional<IWebSearch>();
                    _ = Implements<ITable>("x");
                    _ = SiloNeurons.Get<ITable>(services, "x");
                    _ = Slots.Get<ITable>("customers");
                }
            }
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, CountAmbientReach(sample));

        var contextSample = CSharpSyntaxTree.ParseText("""
            class ContextApp
            {
                void M()
                {
                    _ = Context.Get<ITable>("x");
                    _ = GetOptional<IWebSearch>("w");
                    _ = this.Slots.Get<ITable>("customers");
                    _ = Context.Slots.Get<ITable>("customers");
                }
            }
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, CountAmbientReach(contextSample));
    }

    // Rule #23 (Decision P6): SiloNeurons.Get<T> in src/Modules/* is restricted to a closed privileged allowlist.
    private static readonly HashSet<string> PrivilegedSiloNeuronModules = new(StringComparer.Ordinal)
    {
        "IntoChat.OS.Modules.AI",
        "IntoChat.OS.Modules.Catalog",
        "IntoChat.OS.Modules.Devices",
        "IntoChat.OS.Modules.GitHub",
        "IntoChat.OS.Modules.Google",
        "IntoChat.OS.Modules.Identity",
        "IntoChat.OS.Modules.Mcp",
        "IntoChat.OS.Modules.Postgres",
        "IntoChat.OS.Modules.Roslyn",
        "IntoChat.OS.Modules.Storage",
        "IntoChat.OS.Modules.Windows",
    };

    [Fact]
    public void HouseRule23_SiloNeuronsGetRestrictedToPrivilegedAllowlist()
    {
        var offenders = new List<string>();
        var activeModules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var src in ProductionSources.Value)
        {
            if (!IsModuleCode(src))
            {
                continue;
            }

            var lines = FindSiloNeuronsGetGenericLines(src.Root);
            if (lines.Count == 0)
            {
                continue;
            }

            activeModules.Add(src.ProjectName);
            if (!PrivilegedSiloNeuronModules.Contains(src.ProjectName))
            {
                foreach (var line in lines)
                {
                    offenders.Add($"{DisplayPath(src)}:{line} ({src.ProjectName})");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "SiloNeurons.Get<T> is restricted to PrivilegedSiloNeuronModules (Decision P6):\n" + string.Join('\n', offenders));

        var staleAllowlistEntries = PrivilegedSiloNeuronModules
            .Where(module => !activeModules.Contains(module))
            .OrderBy(static m => m, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            staleAllowlistEntries.Count == 0,
            "PrivilegedSiloNeuronModules contains module(s) with 0 SiloNeurons.Get<T> calls; tighten the allowlist by removing:\n"
            + string.Join('\n', staleAllowlistEntries));
    }

    private static List<int> FindSiloNeuronsGetGenericLines(CompilationUnitSyntax root)
    {
        var lines = new List<int>();
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Get" } } ma)
            {
                var receiver = ma.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
                    _ => null,
                };
                if (receiver == "SiloNeurons")
                {
                    lines.Add(inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }
            }
        }
        return lines;
    }

    private static int CountAmbientReach(SyntaxTree tree)
    {
        var count = 0;
        foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is GenericNameSyntax bare)
            {
                if (IsAmbientMethodName(bare.Identifier.Text))
                {
                    count++;
                }
                continue;
            }

            if (inv.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax member } ma)
            {
                if (ma.Expression is ThisExpressionSyntax or BaseExpressionSyntax)
                {
                    if (IsAmbientMethodName(member.Identifier.Text))
                    {
                        count++;
                    }
                    continue;
                }

                var receiver = ma.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
                    _ => null,
                };

                if (receiver == "Context" && IsAmbientMethodName(member.Identifier.Text))
                {
                    count++;
                }
                else if (receiver == "SiloNeurons" && member.Identifier.Text == "Get")
                {
                    count++;
                }
            }
        }
        return count;

        static bool IsAmbientMethodName(string name) =>
            name is "Get" or "GetOptional" or "TryGetOptional" or "Implements";
    }
}
