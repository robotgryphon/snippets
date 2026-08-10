#!/usr/bin/env dotnet run
#:package Microsoft.CodeAnalysis.CSharp@4.*
#:package System.CommandLine@2.*

// apidiff.cs — public API surface diff for semantic versioning.
//
// Usage:
//   dotnet run apidiff.cs -- --old ./worktree-old --new ./worktree-new --previous-version 1.4.2
//   dotnet run apidiff.cs -- --old a --new b --json report.json --fail-on major
//
// Reads two directories of C# source, builds a canonical public API snapshot of
// each, diffs them, and classifies every change as Major / Minor / Patch.

using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

var oldOption = new Option<DirectoryInfo>("--old", "-o")
{
    Description = "Directory containing the OLD (baseline) source tree.",
    Required = true
};

var newOption = new Option<DirectoryInfo>("--new", "-n")
{
    Description = "Directory containing the NEW (candidate) source tree.",
    Required = true
};

var previousVersionOption = new Option<string?>("--previous-version", "-p")
{
    Description = "Previous version (e.g. 1.4.2). If supplied, a suggested next version is computed."
};

var jsonOption = new Option<string?>("--json")
{
    Description = "Write the JSON report to this path. Use '-' for stdout."
};

var quietOption = new Option<bool>("--quiet", "-q")
{
    Description = "Suppress the human-readable report on stdout."
};

var failOnOption = new Option<Severity>("--fail-on")
{
    Description = "Exit with code 2 if the detected severity is at least this level.",
    DefaultValueFactory = _ => Severity.None
};

var defineOption = new Option<string[]>("--define", "-d")
{
    Description = "Preprocessor symbols to define when parsing (e.g. -d NET10_0 -d RELEASE).",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => []
};

var excludeOption = new Option<string[]>("--exclude", "-x")
{
    Description = "Path substrings to exclude, in addition to bin/obj/.git and generated files.",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => []
};

var langVersionOption = new Option<string>("--lang-version")
{
    Description = "C# language version to parse with (default: latest).",
    DefaultValueFactory = _ => "latest"
};

var showDiagnosticsOption = new Option<bool>("--show-diagnostics")
{
    Description = "Print compiler diagnostics encountered while building the symbol model."
};

var root = new RootCommand(
    "Diffs the public API surface of two C# source trees and classifies changes by semantic-versioning impact.");

root.Options.Add(oldOption);
root.Options.Add(newOption);
root.Options.Add(previousVersionOption);
root.Options.Add(jsonOption);
root.Options.Add(quietOption);
root.Options.Add(failOnOption);
root.Options.Add(defineOption);
root.Options.Add(excludeOption);
root.Options.Add(langVersionOption);
root.Options.Add(showDiagnosticsOption);

root.SetAction(parseResult =>
{
    var oldDir = parseResult.GetValue(oldOption)!;
    var newDir = parseResult.GetValue(newOption)!;
    var previousVersion = parseResult.GetValue(previousVersionOption);
    var jsonPath = parseResult.GetValue(jsonOption);
    var quiet = parseResult.GetValue(quietOption);
    var failOn = parseResult.GetValue(failOnOption);
    var defines = parseResult.GetValue(defineOption) ?? [];
    var excludes = parseResult.GetValue(excludeOption) ?? [];
    var langVersionText = parseResult.GetValue(langVersionOption) ?? "latest";
    var showDiagnostics = parseResult.GetValue(showDiagnosticsOption);

    if (!oldDir.Exists)
    {
        Console.Error.WriteLine($"error: old directory not found: {oldDir.FullName}");
        return 1;
    }

    if (!newDir.Exists)
    {
        Console.Error.WriteLine($"error: new directory not found: {newDir.FullName}");
        return 1;
    }

    if (!LanguageVersionFacts.TryParse(langVersionText, out var langVersion))
    {
        Console.Error.WriteLine($"error: unrecognised language version: {langVersionText}");
        return 1;
    }

    var options = new ScanOptions(defines, excludes, langVersion);

    var oldSnapshot = ApiScanner.Scan(oldDir.FullName, options);
    var newSnapshot = ApiScanner.Scan(newDir.FullName, options);

    var changes = ApiDiffer.Diff(oldSnapshot, newSnapshot);
    var severity = changes.Count == 0 ? Severity.Patch : changes.Max(c => c.Severity);

    string? suggested = null;
    if (!string.IsNullOrWhiteSpace(previousVersion))
    {
        if (SemVer.TryParse(previousVersion, out var parsed))
        {
            suggested = parsed.Bump(severity).ToString();
        }
        else
        {
            Console.Error.WriteLine($"warning: could not parse previous version '{previousVersion}'.");
        }
    }

    var report = new DiffReport(
        OldPath: oldDir.FullName,
        NewPath: newDir.FullName,
        Severity: severity,
        PreviousVersion: previousVersion,
        SuggestedVersion: suggested,
        OldApiCount: oldSnapshot.Entries.Count,
        NewApiCount: newSnapshot.Entries.Count,
        Changes: changes,
        Diagnostics: [.. oldSnapshot.Diagnostics.Select(d => $"[old] {d}"),
                      .. newSnapshot.Diagnostics.Select(d => $"[new] {d}")]);

    if (!quiet)
    {
        Console.Out.Write(Reporting.ToText(report, showDiagnostics));
    }

    if (jsonPath is not null)
    {
        var json = Reporting.ToJson(report);
        if (jsonPath == "-")
        {
            Console.Out.WriteLine(json);
        }
        else
        {
            var full = Path.GetFullPath(jsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json, new UTF8Encoding(false));
            if (!quiet)
            {
                Console.Out.WriteLine($"JSON report written to {full}");
            }
        }
    }

    if (failOn != Severity.None && severity >= failOn)
    {
        return 2;
    }

    return 0;
});

return root.Parse(args).Invoke();

// ---------------------------------------------------------------------------
// Model — this is the "code format" of the result. Everything below is a plain
// record graph, so the diff can be consumed programmatically without going
// through JSON.
// ---------------------------------------------------------------------------

/// <summary>Semantic-versioning impact of a change, ordered least to most severe.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    None = 0,
    Patch = 1,
    Minor = 2,
    Major = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    TypeAdded,
    TypeRemoved,
    MemberAdded,
    MemberRemoved,
    SignatureChanged,
    ReturnTypeChanged,
    AccessibilityNarrowed,
    AccessibilityWidened,
    SealedAdded,
    SealedRemoved,
    AbstractAdded,
    AbstractRemoved,
    VirtualAdded,
    VirtualRemoved,
    StaticChanged,
    RequiredAdded,
    RequiredRemoved,
    BaseTypeChanged,
    InterfaceAdded,
    InterfaceRemoved,
    TypeKindChanged,
    EnumValueChanged,
    ParameterDefaultChanged,
    ParameterNameChanged
}

/// <summary>A single parameter, captured in enough detail to detect binary-breaking edits.</summary>
public sealed record ApiParameter(
    string Name,
    string Type,
    string RefKind,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams);

/// <summary>
/// One entry in the public API surface. <paramref name="Id"/> is the Roslyn
/// documentation-comment declaration ID (e.g. <c>M:Acme.Widget.Resize(System.Int32)</c>)
/// and is the stable key the diff joins on.
/// </summary>
public sealed record ApiEntry(
    string Id,
    string Kind,
    string Signature,
    string? ContainingType,
    string? MemberName,
    string Accessibility,
    bool IsStatic,
    bool IsAbstract,
    bool IsVirtual,
    bool IsSealed,
    bool IsOverride,
    bool IsRequired,
    string? TypeKind,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    string? ReturnType,
    string? ConstantValue,
    IReadOnlyList<ApiParameter> Parameters)
{
    public bool IsType => ContainingType is null || Kind == "Type";
}

public sealed record ApiSnapshot(
    string RootPath,
    int FileCount,
    IReadOnlyDictionary<string, ApiEntry> Entries,
    IReadOnlyList<string> Diagnostics);

public sealed record ApiChange(
    ChangeKind Kind,
    Severity Severity,
    string Id,
    string? Old,
    string? New,
    string Description);

public sealed record DiffReport(
    string OldPath,
    string NewPath,
    Severity Severity,
    string? PreviousVersion,
    string? SuggestedVersion,
    int OldApiCount,
    int NewApiCount,
    IReadOnlyList<ApiChange> Changes,
    IReadOnlyList<string> Diagnostics);

public sealed record ScanOptions(
    string[] Defines,
    string[] Excludes,
    LanguageVersion LanguageVersion);

// ---------------------------------------------------------------------------
// Scanner
// ---------------------------------------------------------------------------

public static class ApiScanner
{
    private static readonly string[] DefaultExcludes =
        [$"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
         $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
         $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"];

    private static readonly string[] GeneratedSuffixes =
        [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs", "assemblyinfo.cs"];

    /// <summary>Canonical rendering of a symbol. Stable across runs; used for display and comparison.</summary>
    public static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
                       | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                     | SymbolDisplayMemberOptions.IncludeType
                     | SymbolDisplayMemberOptions.IncludeContainingType
                     | SymbolDisplayMemberOptions.IncludeExplicitInterface
                     | SymbolDisplayMemberOptions.IncludeModifiers
                     | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                        | SymbolDisplayParameterOptions.IncludeName
                        | SymbolDisplayParameterOptions.IncludeParamsRefOut
                        | SymbolDisplayParameterOptions.IncludeDefaultValue
                        | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat TypeRefFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static ApiSnapshot Scan(string rootPath, ScanOptions options)
    {
        var diagnostics = new List<string>();
        var files = EnumerateSourceFiles(rootPath, options.Excludes).ToList();

        if (files.Count == 0)
        {
            diagnostics.Add("no .cs files found");
        }

        var parseOptions = new CSharpParseOptions(
            languageVersion: options.LanguageVersion,
            preprocessorSymbols: options.Defines);

        var trees = files.Select(path =>
            CSharpSyntaxTree.ParseText(
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                parseOptions,
                path));

        var compilation = CSharpCompilation.Create(
            assemblyName: "ApiSnapshot",
            syntaxTrees: trees,
            references: ReferenceAssemblies(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        var errorCount = compilation
            .GetDiagnostics()
            .Count(d => d.Severity == DiagnosticSeverity.Error);

        if (errorCount > 0)
        {
            diagnostics.Add(
                $"{errorCount} compiler error(s) while building the symbol model; " +
                "unresolved types may be reported by name only. This is expected for source-only scans.");
        }

        var entries = new Dictionary<string, ApiEntry>(StringComparer.Ordinal);
        CollectNamespace(compilation.Assembly.GlobalNamespace, entries);

        return new ApiSnapshot(rootPath, files.Count, entries, diagnostics);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string rootPath, string[] userExcludes)
    {
        foreach (var path in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = path.Replace('/', Path.DirectorySeparatorChar);

            if (DefaultExcludes.Any(e => normalized.Contains(e, StringComparison.OrdinalIgnoreCase)))
                continue;

            var fileName = Path.GetFileName(normalized);
            if (GeneratedSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (userExcludes.Any(e => normalized.Contains(e, StringComparison.OrdinalIgnoreCase)))
                continue;

            yield return path;
        }
    }

    /// <summary>
    /// Uses the reference assemblies of the running host. This resolves BCL types
    /// so that inherited members and special types render correctly. Types from
    /// the project's own NuGet dependencies will still be unresolved.
    /// </summary>
    private static IEnumerable<MetadataReference> ReferenceAssemblies()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa)
            yield break;

        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                MetadataReference? reference = null;
                try { reference = MetadataReference.CreateFromFile(path); }
                catch { /* unreadable assembly — skip */ }
                if (reference is not null) yield return reference;
            }
        }
    }

    private static void CollectNamespace(INamespaceSymbol ns, Dictionary<string, ApiEntry> sink)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            CollectType(type, sink);
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            CollectNamespace(child, sink);
        }
    }

    private static void CollectType(INamedTypeSymbol type, Dictionary<string, ApiEntry> sink)
    {
        if (!IsExposed(type)) return;
        if (IsCompilerName(type.Name)) return;

        var id = DocumentationCommentId.CreateDeclarationId(type);
        if (id is not null)
        {
            sink[id] = new ApiEntry(
                Id: id,
                Kind: "Type",
                Signature: type.ToDisplayString(SignatureFormat),
                ContainingType: type.ContainingType?.ToDisplayString(TypeRefFormat),
                MemberName: type.Name,
                Accessibility: type.DeclaredAccessibility.ToString(),
                IsStatic: type.IsStatic,
                IsAbstract: type.IsAbstract,
                IsVirtual: false,
                IsSealed: type.IsSealed,
                IsOverride: false,
                IsRequired: false,
                TypeKind: DescribeTypeKind(type),
                BaseType: type.BaseType?.ToDisplayString(TypeRefFormat),
                Interfaces: [.. type.AllInterfaces
                    .Where(IsExposed)
                    .Select(i => i.ToDisplayString(TypeRefFormat))
                    .OrderBy(s => s, StringComparer.Ordinal)],
                ReturnType: null,
                ConstantValue: null,
                Parameters: []);
        }

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                CollectType(nested, sink);
                continue;
            }

            CollectMember(member, type, sink);
        }
    }

    private static void CollectMember(ISymbol member, INamedTypeSymbol containing, Dictionary<string, ApiEntry> sink)
    {
        if (!IsExposed(member)) return;
        if (IsCompilerName(member.Name)) return;

        if (member is IMethodSymbol method)
        {
            switch (method.MethodKind)
            {
                case MethodKind.Ordinary:
                case MethodKind.Constructor:
                case MethodKind.UserDefinedOperator:
                case MethodKind.Conversion:
                    break;
                default:
                    return; // accessors, static ctors, finalizers, etc.
            }
        }

        var id = DocumentationCommentId.CreateDeclarationId(member);
        if (id is null) return;

        var parameters = member switch
        {
            IMethodSymbol m => DescribeParameters(m.Parameters),
            IPropertySymbol p => DescribeParameters(p.Parameters),
            _ => (IReadOnlyList<ApiParameter>)[]
        };

        var returnType = member switch
        {
            IMethodSymbol m => m.ReturnsVoid ? "void" : m.ReturnType.ToDisplayString(TypeRefFormat),
            IPropertySymbol p => p.Type.ToDisplayString(TypeRefFormat),
            IFieldSymbol f => f.Type.ToDisplayString(TypeRefFormat),
            IEventSymbol e => e.Type.ToDisplayString(TypeRefFormat),
            _ => null
        };

        string? constant = null;
        if (member is IFieldSymbol { HasConstantValue: true } field)
        {
            constant = Convert.ToString(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture);
        }

        sink[id] = new ApiEntry(
            Id: id,
            Kind: member.Kind.ToString(),
            Signature: member.ToDisplayString(SignatureFormat),
            ContainingType: containing.ToDisplayString(TypeRefFormat),
            MemberName: member.Name,
            Accessibility: member.DeclaredAccessibility.ToString(),
            IsStatic: member.IsStatic,
            IsAbstract: member.IsAbstract,
            IsVirtual: member.IsVirtual,
            IsSealed: member.IsSealed,
            IsOverride: member.IsOverride,
            IsRequired: member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true },
            TypeKind: null,
            BaseType: null,
            Interfaces: [],
            ReturnType: returnType,
            ConstantValue: constant,
            Parameters: parameters);
    }

    private static IReadOnlyList<ApiParameter> DescribeParameters(IEnumerable<IParameterSymbol> parameters) =>
        [.. parameters.Select(p => new ApiParameter(
            Name: p.Name,
            Type: p.Type.ToDisplayString(TypeRefFormat),
            RefKind: p.RefKind.ToString(),
            IsOptional: p.HasExplicitDefaultValue,
            DefaultValue: p.HasExplicitDefaultValue
                ? Convert.ToString(p.ExplicitDefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
                : null,
            IsParams: p.IsParams))];

    private static string DescribeTypeKind(INamedTypeSymbol type) =>
        type.TypeKind switch
        {
            TypeKind.Class when type.IsRecord => "record",
            TypeKind.Struct when type.IsRecord => "record struct",
            TypeKind.Struct when type.IsReadOnly => "readonly struct",
            _ => type.TypeKind.ToString().ToLowerInvariant()
        };

    /// <summary>True when the symbol and every containing type are visible outside the assembly.</summary>
    private static bool IsExposed(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public
                or Accessibility.Protected
                or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCompilerName(string name) =>
        name.Contains('<') || name.Contains('$');
}

// ---------------------------------------------------------------------------
// Differ
// ---------------------------------------------------------------------------

public static class ApiDiffer
{
    public static IReadOnlyList<ApiChange> Diff(ApiSnapshot oldSnapshot, ApiSnapshot newSnapshot)
    {
        var changes = new List<ApiChange>();

        var oldEntries = oldSnapshot.Entries;
        var newEntries = newSnapshot.Entries;

        var removed = oldEntries.Values.Where(e => !newEntries.ContainsKey(e.Id)).ToList();
        var added = newEntries.Values.Where(e => !oldEntries.ContainsKey(e.Id)).ToList();

        // Pair removed/added members that share a containing type and name — these
        // are almost always a signature edit rather than a genuine remove + add.
        var paired = PairSignatureChanges(removed, added, changes);
        removed = [.. removed.Where(e => !paired.Contains(e.Id))];
        added = [.. added.Where(e => !paired.Contains(e.Id))];

        foreach (var entry in removed.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            changes.Add(entry.IsType
                ? new ApiChange(ChangeKind.TypeRemoved, Severity.Major, entry.Id, entry.Signature, null,
                    $"Public type removed: {entry.Signature}")
                : new ApiChange(ChangeKind.MemberRemoved, Severity.Major, entry.Id, entry.Signature, null,
                    $"Public member removed: {entry.Signature}"));
        }

        foreach (var entry in added.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            changes.Add(entry.IsType
                ? new ApiChange(ChangeKind.TypeAdded, Severity.Minor, entry.Id, null, entry.Signature,
                    $"New public type: {entry.Signature}")
                : ClassifyAddedMember(entry, newEntries));
        }

        foreach (var (id, oldEntry) in oldEntries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!newEntries.TryGetValue(id, out var newEntry)) continue;
            CompareExisting(oldEntry, newEntry, changes);
        }

        return [.. changes.OrderByDescending(c => c.Severity).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    private static HashSet<string> PairSignatureChanges(
        List<ApiEntry> removed, List<ApiEntry> added, List<ApiChange> changes)
    {
        var paired = new HashSet<string>(StringComparer.Ordinal);

        static string Key(ApiEntry e) => $"{e.ContainingType}\u0000{e.MemberName}\u0000{e.Kind}";

        var removedByKey = removed
            .Where(e => !e.IsType)
            .GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var addedByKey = added
            .Where(e => !e.IsType)
            .GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        foreach (var (key, removedGroup) in removedByKey)
        {
            if (!addedByKey.TryGetValue(key, out var addedGroup)) continue;

            var count = Math.Min(removedGroup.Count, addedGroup.Count);
            for (var i = 0; i < count; i++)
            {
                var before = removedGroup[i];
                var after = addedGroup[i];

                changes.Add(new ApiChange(
                    ChangeKind.SignatureChanged,
                    Severity.Major,
                    before.Id,
                    before.Signature,
                    after.Signature,
                    $"Signature changed: {before.Signature} -> {after.Signature}"));

                paired.Add(before.Id);
                paired.Add(after.Id);
            }
        }

        return paired;
    }

    private static ApiChange ClassifyAddedMember(ApiEntry entry, IReadOnlyDictionary<string, ApiEntry> newEntries)
    {
        var containing = FindContainingType(entry, newEntries);
        var severity = Severity.Minor;
        var note = string.Empty;

        if (containing?.TypeKind == "interface")
        {
            // Abstract interface member => every implementor breaks.
            // Default implementation => additive.
            if (entry.IsAbstract)
            {
                severity = Severity.Major;
                note = " (new abstract interface member breaks existing implementors)";
            }
        }
        else if (entry.IsAbstract)
        {
            severity = Severity.Major;
            note = " (new abstract member breaks existing derived types)";
        }
        else if (entry.IsRequired)
        {
            severity = Severity.Major;
            note = " (new required member breaks existing object initializers)";
        }

        return new ApiChange(
            ChangeKind.MemberAdded,
            severity,
            entry.Id,
            null,
            entry.Signature,
            $"New public member: {entry.Signature}{note}");
    }

    private static ApiEntry? FindContainingType(ApiEntry entry, IReadOnlyDictionary<string, ApiEntry> entries)
    {
        if (entry.ContainingType is null) return null;
        var typeId = "T:" + entry.ContainingType.Replace('<', '{').Replace('>', '}');
        return entries.TryGetValue(typeId, out var found)
            ? found
            : entries.Values.FirstOrDefault(e =>
                e.IsType && string.Equals(e.Signature, entry.ContainingType, StringComparison.Ordinal));
    }

    private static void CompareExisting(ApiEntry before, ApiEntry after, List<ApiChange> changes)
    {
        void Add(ChangeKind kind, Severity severity, string? oldValue, string? newValue, string description) =>
            changes.Add(new ApiChange(kind, severity, before.Id, oldValue, newValue,
                $"{before.Signature}: {description}"));

        if (before.Accessibility != after.Accessibility)
        {
            var narrowed = AccessibilityRank(after.Accessibility) < AccessibilityRank(before.Accessibility);
            Add(narrowed ? ChangeKind.AccessibilityNarrowed : ChangeKind.AccessibilityWidened,
                narrowed ? Severity.Major : Severity.Minor,
                before.Accessibility, after.Accessibility,
                $"accessibility changed from {before.Accessibility} to {after.Accessibility}");
        }

        if (before.IsSealed != after.IsSealed)
        {
            Add(after.IsSealed ? ChangeKind.SealedAdded : ChangeKind.SealedRemoved,
                after.IsSealed ? Severity.Major : Severity.Minor,
                before.IsSealed.ToString(), after.IsSealed.ToString(),
                after.IsSealed ? "became sealed" : "is no longer sealed");
        }

        if (before.IsAbstract != after.IsAbstract)
        {
            Add(after.IsAbstract ? ChangeKind.AbstractAdded : ChangeKind.AbstractRemoved,
                after.IsAbstract ? Severity.Major : Severity.Minor,
                before.IsAbstract.ToString(), after.IsAbstract.ToString(),
                after.IsAbstract ? "became abstract" : "is no longer abstract");
        }

        if (before.IsVirtual != after.IsVirtual)
        {
            Add(after.IsVirtual ? ChangeKind.VirtualAdded : ChangeKind.VirtualRemoved,
                after.IsVirtual ? Severity.Minor : Severity.Major,
                before.IsVirtual.ToString(), after.IsVirtual.ToString(),
                after.IsVirtual ? "became virtual" : "is no longer virtual (existing overrides break)");
        }

        if (before.IsStatic != after.IsStatic)
        {
            Add(ChangeKind.StaticChanged, Severity.Major,
                before.IsStatic.ToString(), after.IsStatic.ToString(),
                after.IsStatic ? "became static" : "is no longer static");
        }

        if (before.IsRequired != after.IsRequired)
        {
            Add(after.IsRequired ? ChangeKind.RequiredAdded : ChangeKind.RequiredRemoved,
                after.IsRequired ? Severity.Major : Severity.Minor,
                before.IsRequired.ToString(), after.IsRequired.ToString(),
                after.IsRequired ? "became required" : "is no longer required");
        }

        if (before.TypeKind != after.TypeKind)
        {
            Add(ChangeKind.TypeKindChanged, Severity.Major,
                before.TypeKind, after.TypeKind,
                $"type kind changed from {before.TypeKind} to {after.TypeKind}");
        }

        if (before.BaseType != after.BaseType)
        {
            Add(ChangeKind.BaseTypeChanged, Severity.Major,
                before.BaseType, after.BaseType,
                $"base type changed from {before.BaseType ?? "(none)"} to {after.BaseType ?? "(none)"}");
        }

        foreach (var lost in before.Interfaces.Except(after.Interfaces, StringComparer.Ordinal))
        {
            Add(ChangeKind.InterfaceRemoved, Severity.Major, lost, null,
                $"no longer implements {lost}");
        }

        foreach (var gained in after.Interfaces.Except(before.Interfaces, StringComparer.Ordinal))
        {
            Add(ChangeKind.InterfaceAdded, Severity.Minor, null, gained,
                $"now implements {gained}");
        }

        // Declaration IDs do not encode the return type, so this is a real detection.
        if (before.ReturnType != after.ReturnType)
        {
            Add(ChangeKind.ReturnTypeChanged, Severity.Major,
                before.ReturnType, after.ReturnType,
                $"type changed from {before.ReturnType} to {after.ReturnType}");
        }

        if (before.ConstantValue != after.ConstantValue)
        {
            Add(ChangeKind.EnumValueChanged, Severity.Major,
                before.ConstantValue, after.ConstantValue,
                $"constant value changed from {before.ConstantValue} to {after.ConstantValue}");
        }

        CompareParameters(before, after, changes);
    }

    private static void CompareParameters(ApiEntry before, ApiEntry after, List<ApiChange> changes)
    {
        // Parameter *types* are part of the declaration ID, so matched entries always
        // agree on arity and types. Only names and defaults can differ here.
        var count = Math.Min(before.Parameters.Count, after.Parameters.Count);

        for (var i = 0; i < count; i++)
        {
            var oldParam = before.Parameters[i];
            var newParam = after.Parameters[i];

            if (oldParam.IsOptional != newParam.IsOptional || oldParam.DefaultValue != newParam.DefaultValue)
            {
                changes.Add(new ApiChange(
                    ChangeKind.ParameterDefaultChanged,
                    Severity.Major,
                    before.Id,
                    $"{oldParam.Name} = {oldParam.DefaultValue ?? "(none)"}",
                    $"{newParam.Name} = {newParam.DefaultValue ?? "(none)"}",
                    $"{before.Signature}: default value of parameter '{newParam.Name}' changed " +
                    "(callers compiled against the old default keep the old value)"));
            }

            if (oldParam.Name != newParam.Name)
            {
                changes.Add(new ApiChange(
                    ChangeKind.ParameterNameChanged,
                    Severity.Minor,
                    before.Id,
                    oldParam.Name,
                    newParam.Name,
                    $"{before.Signature}: parameter renamed from '{oldParam.Name}' to '{newParam.Name}' " +
                    "(source-breaking for callers using named arguments)"));
            }
        }
    }

    private static int AccessibilityRank(string accessibility) => accessibility switch
    {
        "Public" => 4,
        "ProtectedOrInternal" => 3,
        "Protected" => 2,
        _ => 1
    };
}

// ---------------------------------------------------------------------------
// Version arithmetic
// ---------------------------------------------------------------------------

public readonly record struct SemVer(int Major, int Minor, int Patch, string? Prerelease, string? Build)
{
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim().TrimStart('v', 'V');

        string? build = null;
        var buildIndex = value.IndexOf('+');
        if (buildIndex >= 0)
        {
            build = value[(buildIndex + 1)..];
            value = value[..buildIndex];
        }

        string? prerelease = null;
        var preIndex = value.IndexOf('-');
        if (preIndex >= 0)
        {
            prerelease = value[(preIndex + 1)..];
            value = value[..preIndex];
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        if (!int.TryParse(parts[0], out var major)) return false;
        var minor = 0;
        var patch = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length > 2 && !int.TryParse(parts[2], out patch)) return false;

        version = new SemVer(major, minor, patch, prerelease, build);
        return true;
    }

    /// <summary>
    /// Applies a severity to this version. Note the 0.x convention: while the major
    /// version is 0 the API is considered unstable, so a breaking change bumps the
    /// minor rather than promoting the package to 1.0.0.
    /// </summary>
    public SemVer Bump(Severity severity)
    {
        if (Major == 0)
        {
            return severity switch
            {
                Severity.Major => new SemVer(0, Minor + 1, 0, null, null),
                Severity.Minor => new SemVer(0, Minor, Patch + 1, null, null),
                _ => new SemVer(0, Minor, Patch + 1, null, null)
            };
        }

        return severity switch
        {
            Severity.Major => new SemVer(Major + 1, 0, 0, null, null),
            Severity.Minor => new SemVer(Major, Minor + 1, 0, null, null),
            _ => new SemVer(Major, Minor, Patch + 1, null, null)
        };
    }

    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease)) text += $"-{Prerelease}";
        if (!string.IsNullOrEmpty(Build)) text += $"+{Build}";
        return text;
    }
}

// ---------------------------------------------------------------------------
// Reporting
// ---------------------------------------------------------------------------

public static class Reporting
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(DiffReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    public static string ToText(DiffReport report, bool showDiagnostics)
    {
        var sb = new StringBuilder();

        sb.AppendLine("API surface diff");
        sb.AppendLine($"  old: {report.OldPath} ({report.OldApiCount} public declarations)");
        sb.AppendLine($"  new: {report.NewPath} ({report.NewApiCount} public declarations)");
        sb.AppendLine();

        if (report.Changes.Count == 0)
        {
            sb.AppendLine("No public API changes detected.");
        }
        else
        {
            foreach (var group in report.Changes
                         .GroupBy(c => c.Severity)
                         .OrderByDescending(g => g.Key))
            {
                sb.AppendLine($"{group.Key.ToString().ToUpperInvariant()} ({group.Count()})");
                foreach (var change in group)
                {
                    sb.AppendLine($"  [{change.Kind}] {change.Description}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine($"Severity: {report.Severity}");

        if (report.PreviousVersion is not null)
        {
            sb.AppendLine($"Previous version:  {report.PreviousVersion}");
            sb.AppendLine($"Suggested version: {report.SuggestedVersion ?? "(could not compute)"}");
        }

        if (showDiagnostics && report.Diagnostics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                sb.AppendLine($"  {diagnostic}");
            }
        }

        return sb.ToString();
    }
}
