using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ComplexResources.Generator;

internal static class Diagnostics
{
    private const string Category = "ComplexResources";

    public static readonly DiagnosticDescriptor BadContract = Error(
        "CR0001", "Contract must be a single-parameter generic interface",
        "The contract on '{0}' must be a generic interface with exactly one type parameter such as IStateReader<T>");

    public static readonly DiagnosticDescriptor NoSubResources = Error(
        "CR0002", "Resource has no sub-resources",
        "The resource '{0}' declares no [SubResource] members; there is nothing to fan out to");

    public static readonly DiagnosticDescriptor UnsupportedMethod = Error(
        "CR0003", "Unsupported contract method",
        "Contract method '{0}' cannot be forwarded: {1}");

    private static DiagnosticDescriptor Error(string id, string title, string messageFormat)
        => new(id, title, messageFormat, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "CR0001" => BadContract,
        "CR0002" => NoSubResources,
        _ => UnsupportedMethod,
    };
}

internal enum ReturnShape
{
    Unsupported = 0,
    ValueTaskVoid,
    ValueTaskResult,
    TaskVoid,
    TaskResult,
}

/// <summary>A sub-resource of the complex resource, and the closed sub-service to forward to.</summary>
internal sealed record SubResourceRef(
    string MemberName,          // e.g. "Local" — projected as resource.Local
    string ParameterName,       // e.g. "local" — constructor parameter
    string FieldName,           // e.g. "_local"
    string SubServiceTypeFqn);  // e.g. global::...IStateWriter<global::...LocalUser>

/// <summary>An injected IMergeHandler for one distinct result type of a service.</summary>
internal sealed record MergeHandlerRef(
    string ResultTypeFqn,       // e.g. global::...State
    string ParameterName,       // e.g. "mergeState"
    string FieldName);          // e.g. "_mergeState"

internal sealed record ParamModel(string TypeFqn, string Name, bool IsResource);

internal sealed record MethodModel(
    string Name,
    ReturnShape Shape,
    string? ResultTypeFqn,      // R for result shapes; null for void shapes (also the merge host)
    string ReturnTypeFqn,       // full return type, e.g. global::...ValueTask<global::...State>
    string ResourceParameterName,
    EquatableArray<ParamModel> Parameters)
{
    public bool HasResult => Shape is ReturnShape.ValueTaskResult or ReturnShape.TaskResult;
    public bool NeedsAsTask => Shape is ReturnShape.ValueTaskVoid or ReturnShape.ValueTaskResult;
}

/// <summary>One generated service class: a contract closed over the resource.</summary>
internal sealed record ServiceSpec(
    string TypeName,
    string ContractClosedFqn,   // e.g. global::...IStateWriter<global::...User>
    bool ConstructorIsPrivate,  // true when the author declares their own ctor to chain to this one
    EquatableArray<SubResourceRef> Subs,
    EquatableArray<MergeHandlerRef> MergeHandlers,
    EquatableArray<MethodModel> Methods,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>A [ComplexResource] and every service requested on it; fully value-equatable for caching.</summary>
internal sealed record ResourceModel(
    string? Namespace,
    LocationInfo? Location,
    EquatableArray<ServiceSpec> Services,
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>Value-equatable diagnostic carried out of the transform and reported downstream.</summary>
internal sealed record DiagnosticInfo(string DescriptorId, LocationInfo? Location, EquatableArray<string> MessageArgs)
{
    public static DiagnosticInfo Create(string id, LocationInfo? location, params string[] args)
        => new(id, location, new EquatableArray<string>(ImmutableArray.CreateRange(args)));

    public Diagnostic ToDiagnostic()
        => Diagnostic.Create(
            Diagnostics.ById(DescriptorId),
            Location?.ToLocation(),
            MessageArgs.Array.Select(a => (object)a).ToArray());
}

/// <summary>Serializable, value-equatable stand-in for <see cref="Location"/>.</summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    public static LocationInfo? From(Location? location)
        => location?.SourceTree is { } tree
            ? new LocationInfo(tree.FilePath, location.SourceSpan, location.GetLineSpan().Span)
            : null;
}

/// <summary>An <see cref="ImmutableArray{T}"/> with structural equality, so models cache correctly.</summary>
internal readonly struct EquatableArray<T> : System.IEquatable<EquatableArray<T>>
    where T : System.IEquatable<T>
{
    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array) => _array = array;

    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    public ImmutableArray<T> Array => _array.IsDefault ? ImmutableArray<T>.Empty : _array;
    public int Count => Array.Length;

    public static EquatableArray<T> From(IEnumerable<T> items) => new(ImmutableArray.CreateRange(items));

    public bool Equals(EquatableArray<T> other)
    {
        ImmutableArray<T> a = Array, b = other.Array;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (T item in Array)
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        return hash;
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => Array.GetEnumerator();
}
