using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ComplexResources.Generator;

/// <summary>
/// For a [ComplexResource] type carrying <c>[GenerateComplexService(typeof(IContract&lt;&gt;))]</c>,
/// emits a complete implementation of each contract: every method is forwarded to one sub-service per
/// <c>[SubResource]</c>, results are collected, and result-returning methods are folded inline via the
/// result type's <c>IMergeable&lt;T&gt;.Merge</c>. No hand-written class or merge is needed.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ComplexResourceGenerator : IIncrementalGenerator
{
    private const string ServiceAttribute = "ComplexResources.GenerateComplexServiceAttribute";
    private const string SubResourceAttribute = "ComplexResources.SubResourceAttribute";
    private const string MergeableInterface = "IMergeable";
    private const string MergeableNamespace = "ComplexResources";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            ServiceAttribute,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, ct) => Parse(ctx, ct));

        context.RegisterSourceOutput(models, static (spc, model) => Emit(spc, model));
    }

    // ---- parse ----

    private static ResourceModel Parse(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var resource = (INamedTypeSymbol)ctx.TargetSymbol;
        var node = (TypeDeclarationSyntax)ctx.TargetNode;
        var resourceLocation = LocationInfo.From(node.Identifier.GetLocation());
        var ns = resource.ContainingNamespace.IsGlobalNamespace ? null : resource.ContainingNamespace.ToDisplayString();

        var subResources = CollectSubResources(resource);
        if (subResources.Count == 0)
        {
            return new ResourceModel(ns, resourceLocation, EquatableArray<ServiceSpec>.Empty,
                EquatableArray<DiagnosticInfo>.From(new[] { DiagnosticInfo.Create("CR0002", resourceLocation, resource.Name) }));
        }

        var services = new List<ServiceSpec>();
        foreach (var attribute in ctx.Attributes)
        {
            ct.ThrowIfCancellationRequested();
            services.Add(BuildService(attribute, resource, subResources, ns, resourceLocation, ctx.SemanticModel.Compilation, ct));
        }

        return new ResourceModel(ns, resourceLocation, EquatableArray<ServiceSpec>.From(services), EquatableArray<DiagnosticInfo>.Empty);
    }

    private static ServiceSpec BuildService(
        AttributeData attribute,
        INamedTypeSymbol resource,
        List<(ITypeSymbol Type, string Name)> subResources,
        string? ns,
        LocationInfo? resourceLocation,
        Compilation compilation,
        CancellationToken ct)
    {
        var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation()) ?? resourceLocation;
        var diagnostics = new List<DiagnosticInfo>();

        var contractDef = (attribute.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol)?.OriginalDefinition;
        var typeName = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Name").Value.Value as string;

        if (contractDef is null || contractDef.TypeKind != TypeKind.Interface || contractDef.Arity != 1)
        {
            diagnostics.Add(DiagnosticInfo.Create("CR0001", location, resource.Name));
            return new ServiceSpec(typeName ?? "Complex", "", false, EquatableArray<SubResourceRef>.Empty,
                EquatableArray<MethodModel>.Empty, EquatableArray<DiagnosticInfo>.From(diagnostics));
        }

        typeName ??= DefaultName(contractDef.Name);

        // If the author declares their own constructor (to take extra dependencies), make ours private
        // so their public constructor can chain to it: `: this(local, remote)`.
        var existing = compilation.GetTypeByMetadataName(ns is null ? typeName : $"{ns}.{typeName}");
        var constructorIsPrivate = existing?.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared) ?? false;

        var subs = subResources.Select(s => new SubResourceRef(
            MemberName: s.Name,
            ParameterName: ToParameterName(s.Name),
            FieldName: "_" + ToParameterName(s.Name),
            SubServiceTypeFqn: Fqn(contractDef.Construct(s.Type)))).ToList();

        var methods = BuildMethods(contractDef, resource, location, diagnostics, ct);

        return new ServiceSpec(
            typeName,
            Fqn(contractDef.Construct(resource)),
            constructorIsPrivate,
            EquatableArray<SubResourceRef>.From(subs),
            EquatableArray<MethodModel>.From(methods),
            EquatableArray<DiagnosticInfo>.From(diagnostics));
    }

    private static List<MethodModel> BuildMethods(
        INamedTypeSymbol contractDef,
        INamedTypeSymbol resource,
        LocationInfo? location,
        List<DiagnosticInfo> diagnostics,
        CancellationToken ct)
    {
        var typeParam = contractDef.TypeParameters[0];
        var closed = contractDef.Construct(resource);

        static List<IMethodSymbol> Methods(INamedTypeSymbol type) => type
            .GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic)
            .ToList();

        var definitionMethods = Methods(contractDef);
        var closedMethods = Methods(closed);
        var models = new List<MethodModel>();

        for (var i = 0; i < definitionMethods.Count && i < closedMethods.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var def = definitionMethods[i];
            var closedMethod = closedMethods[i];

            var resourceIndex = ResourceParameterIndex(def.Parameters, typeParam);
            if (resourceIndex < 0)
            {
                diagnostics.Add(DiagnosticInfo.Create("CR0003", location, def.Name, "it has no parameter of the resource type"));
                continue;
            }

            if (closedMethod.Parameters.Any(p => p.RefKind != RefKind.None))
            {
                diagnostics.Add(DiagnosticInfo.Create("CR0003", location, def.Name, "ref/out/in parameters are not supported"));
                continue;
            }

            var (shape, result) = Classify(closedMethod.ReturnType);
            if (shape == ReturnShape.Unsupported)
            {
                diagnostics.Add(DiagnosticInfo.Create("CR0003", location, def.Name,
                    $"unsupported return type '{closedMethod.ReturnType.ToDisplayString()}' (expected Task/ValueTask, optionally of a result)"));
                continue;
            }

            if (result is not null && !IsMergeable(result))
            {
                diagnostics.Add(DiagnosticInfo.Create("CR0004", location, result.ToDisplayString(), closedMethod.Name));
                continue;
            }

            var parameters = closedMethod.Parameters
                .Select((p, idx) => new ParamModel(Fqn(p.Type), p.Name, idx == resourceIndex))
                .ToList();

            models.Add(new MethodModel(
                closedMethod.Name,
                shape,
                result is null ? null : Fqn(result),
                Fqn(closedMethod.ReturnType),
                closedMethod.Parameters[resourceIndex].Name,
                EquatableArray<ParamModel>.From(parameters)));
        }

        return models;
    }

    private static List<(ITypeSymbol Type, string Name)> CollectSubResources(INamedTypeSymbol resource)
    {
        var subs = new List<(ITypeSymbol, string)>();
        var seen = new HashSet<string>();

        void Add(ITypeSymbol type, string name)
        {
            if (seen.Add(name)) subs.Add((type, name));
        }

        foreach (var member in resource.GetMembers())
        {
            if (member.IsStatic || !HasSubResourceAttribute(member)) continue;
            switch (member)
            {
                case IPropertySymbol p: Add(p.Type, p.Name); break;
                case IFieldSymbol f when !f.IsImplicitlyDeclared: Add(f.Type, f.Name); break;
            }
        }

        foreach (var ctor in resource.InstanceConstructors)
            foreach (var param in ctor.Parameters)
                if (HasSubResourceAttribute(param))
                    Add(param.Type, param.Name);

        return subs;
    }

    private static (ReturnShape, ITypeSymbol?) Classify(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol named ||
            named.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks")
            return (ReturnShape.Unsupported, null);

        return (named.Name, named.Arity) switch
        {
            ("ValueTask", 0) => (ReturnShape.ValueTaskVoid, null),
            ("ValueTask", 1) => (ReturnShape.ValueTaskResult, named.TypeArguments[0]),
            ("Task", 0) => (ReturnShape.TaskVoid, null),
            ("Task", 1) => (ReturnShape.TaskResult, named.TypeArguments[0]),
            _ => (ReturnShape.Unsupported, null),
        };
    }

    private static bool IsMergeable(ITypeSymbol result)
        => result.AllInterfaces.Any(i =>
            i.OriginalDefinition.Name == MergeableInterface &&
            i.OriginalDefinition.ContainingNamespace?.ToDisplayString() == MergeableNamespace &&
            i.TypeArguments.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], result));

    private static int ResourceParameterIndex(ImmutableArray<IParameterSymbol> parameters, ITypeParameterSymbol typeParam)
    {
        for (var i = 0; i < parameters.Length; i++)
            if (SymbolEqualityComparer.Default.Equals(parameters[i].Type, typeParam))
                return i;
        return -1;
    }

    private static bool HasSubResourceAttribute(ISymbol symbol)
        => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == SubResourceAttribute);

    private static string DefaultName(string interfaceName)
    {
        var core = interfaceName.Length >= 2 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1])
            ? interfaceName.Substring(1)
            : interfaceName;
        return "Complex" + core;
    }

    private static string Fqn(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string ToParameterName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "arg";
        var camel = char.ToLowerInvariant(name[0]) + name.Substring(1);
        return SyntaxFacts.GetKeywordKind(camel) != SyntaxKind.None ? "@" + camel : camel;
    }

    // ---- emit ----

    private static void Emit(SourceProductionContext spc, ResourceModel model)
    {
        var resourceBlocked = false;
        foreach (var info in model.Diagnostics)
        {
            var diagnostic = info.ToDiagnostic();
            if (diagnostic.Severity == DiagnosticSeverity.Error) resourceBlocked = true;
            spc.ReportDiagnostic(diagnostic);
        }

        if (resourceBlocked) return;

        foreach (var service in model.Services)
        {
            var blocked = false;
            foreach (var info in service.Diagnostics)
            {
                var diagnostic = info.ToDiagnostic();
                // CR0001 (bad contract) blocks the whole service; per-method CR0003/CR0004 only skip
                // that method (already excluded) so the rest can still generate.
                if (diagnostic.Id == "CR0001") blocked = true;
                spc.ReportDiagnostic(diagnostic);
            }

            if (blocked || service.Methods.Count == 0) continue;

            spc.AddSource($"{(model.Namespace is null ? "" : model.Namespace + ".")}{service.TypeName}.g.cs",
                Render(model.Namespace, service));
        }
    }

    private static string Render(string? ns, ServiceSpec service)
    {
        var subs = service.Subs.Array;
        var indent = ns is null ? "" : "    ";
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (ns is not null)
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        sb.AppendLine($"{indent}public sealed partial class {service.TypeName} : {service.ContractClosedFqn}");
        sb.AppendLine($"{indent}{{");

        foreach (var sub in subs)
            sb.AppendLine($"{indent}    private readonly {sub.SubServiceTypeFqn} {sub.FieldName};");
        sb.AppendLine();

        // Private when the author declares their own constructor, so they can chain: `: this(...)`
        // and add extra dependencies; public otherwise so it is the injectable constructor.
        var access = service.ConstructorIsPrivate ? "private" : "public";
        sb.AppendLine($"{indent}    {access} {service.TypeName}(");
        for (var i = 0; i < subs.Length; i++)
            sb.AppendLine($"{indent}        {subs[i].SubServiceTypeFqn} {subs[i].ParameterName}{(i == subs.Length - 1 ? ")" : ",")}");
        sb.AppendLine($"{indent}    {{");
        foreach (var sub in subs)
            sb.AppendLine($"{indent}        {sub.FieldName} = {sub.ParameterName};");
        sb.AppendLine($"{indent}    }}");

        foreach (var method in service.Methods.Array)
        {
            sb.AppendLine();
            RenderMethod(sb, indent, subs, method);
        }

        sb.AppendLine($"{indent}}}");
        if (ns is not null)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static void RenderMethod(StringBuilder sb, string indent, ImmutableArray<SubResourceRef> subs, MethodModel method)
    {
        var parameters = method.Parameters.Array;
        var paramList = string.Join(", ", parameters.Select(p => $"{p.TypeFqn} {p.Name}"));

        string CallArgs(SubResourceRef sub) => string.Join(", ", parameters.Select(p =>
            p.IsResource ? $"{method.ResourceParameterName}.{sub.MemberName}" : p.Name));

        string Call(SubResourceRef sub)
        {
            var call = $"{sub.FieldName}.{method.Name}({CallArgs(sub)})";
            return method.NeedsAsTask ? $"{call}.AsTask()" : call;
        }

        var whenAll = "global::System.Threading.Tasks.Task.WhenAll(" + string.Join(", ", subs.Select(Call)) + ")";

        sb.AppendLine($"{indent}    public async {method.ReturnTypeFqn} {method.Name}({paramList})");
        sb.AppendLine($"{indent}    {{");
        if (method.HasResult)
        {
            sb.AppendLine($"{indent}        var __results = await {whenAll}.ConfigureAwait(false);");
            sb.AppendLine($"{indent}        return {method.ResultTypeFqn}.Merge(__results);");
        }
        else
        {
            sb.AppendLine($"{indent}        await {whenAll}.ConfigureAwait(false);");
        }
        sb.AppendLine($"{indent}    }}");
    }
}
