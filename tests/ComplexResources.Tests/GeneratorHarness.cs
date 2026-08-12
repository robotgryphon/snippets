using System.Collections.Immutable;
using System.Reflection;
using ComplexResources.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ComplexResources.Tests;

internal sealed record GeneratorRun(
    Compilation Output,
    ImmutableArray<Diagnostic> Diagnostics,
    IReadOnlyList<string> GeneratedSources,
    IReadOnlyList<Diagnostic> CompileErrors)
{
    public bool HasError(string id)
        => Diagnostics.Any(d => d.Id == id && d.Severity == DiagnosticSeverity.Error);

    public string SingleGenerated() => Assert.Single(GeneratedSources);
}

internal static class GeneratorHarness
{
    private static readonly string[] FrameworkAssemblies =
    {
        "System.Private.CoreLib", "System.Runtime", "netstandard",
        "System.Collections", "System.Linq", "System.Threading",
        "System.Threading.Tasks", "System.Runtime.Extensions",
    };

    public static GeneratorRun Run(string source)
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .ToArray();

        string? Locate(string name) => trusted.FirstOrDefault(
            p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase));

        var references = FrameworkAssemblies
            .Select(Locate)
            .Where(p => p is not null)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p!))
            .ToList();

        // The attributes assembly ([GenerateComplexService], [ComplexResource], [SubResource]).
        references.Add(MetadataReference.CreateFromFile(
            typeof(ComplexResourceAttribute).Assembly.Location));

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var inputTree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var compilation = CSharpCompilation.Create(
            "GeneratorTestAsm",
            new[] { inputTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        CSharpGeneratorDriver
            .Create(new[] { new ComplexResourceGenerator().AsSourceGenerator() }, parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var generated = output.SyntaxTrees
            .Where(t => t != inputTree)
            .Select(t => t.ToString())
            .ToList();

        var compileErrors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        return new GeneratorRun(output, diagnostics, generated, compileErrors);
    }

    /// <summary>Emits the generated compilation to an in-memory assembly and loads it, so tests can
    /// actually run the fan-out. Requires the run to be compile-clean.</summary>
    public static Assembly EmitAndLoad(GeneratorRun run)
    {
        Assert.Empty(run.CompileErrors);
        using var stream = new MemoryStream();
        var result = run.Output.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
}
