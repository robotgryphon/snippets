namespace ComplexResources;

/// <summary>Marks a type as a complex resource: one that decomposes into several
/// <see cref="SubResourceAttribute"/> members treated as a single resource.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ComplexResourceAttribute : Attribute { }

/// <summary>Marks a member (or positional record parameter) of a
/// [ComplexResource] type as one of its sub-resources.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SubResourceAttribute : Attribute { }

/// <summary>
/// Placed on a [ComplexResource] type to have a complete implementation of <paramref name="contract"/>
/// generated for it: every contract method is forwarded to one sub-service per sub-resource, results
/// are collected, and each result-returning method is folded via the result type's
/// <see cref="IMergeable{TSelf}.Merge"/>. Apply once per contract.
/// </summary>
/// <example><code>
/// [ComplexResource]
/// [GenerateComplexService(typeof(IStateReader&lt;&gt;))]
/// [GenerateComplexService(typeof(IStateWriter&lt;&gt;))]
/// public readonly partial record struct User(
///     [property: SubResource] LocalUser Local,
///     [property: SubResource] RemoteUser Remote);
/// </code></example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class GenerateComplexServiceAttribute : Attribute
{
    public GenerateComplexServiceAttribute(Type contract) => Contract = contract;

    /// <summary>The open generic service interface, e.g. <c>typeof(IStateWriter&lt;&gt;)</c>.</summary>
    public Type Contract { get; }

    /// <summary>Name of the generated class. Defaults to <c>Complex{contract without leading I}</c>,
    /// e.g. <c>ComplexStateWriter</c> for <c>IStateWriter&lt;&gt;</c>.</summary>
    public string? Name { get; set; }
}
