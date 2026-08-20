using System.Reflection;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using HotChocolate.Fusion.Aspire;

namespace ExternalGraphQL;

/// <summary>
/// Constructs HotChocolate's internal <c>GraphQLSourceSchemaAnnotation</c> by reflection, so every
/// property — <c>Location</c> included — can be set independently.
/// </summary>
internal static class GraphQLSourceSchemaAnnotationFactory
{
    private const string AnnotationTypeName = "HotChocolate.Fusion.Aspire.GraphQLSourceSchemaAnnotation";
    private const string LocationTypeName = "HotChocolate.Fusion.Aspire.SourceSchemaLocationType";

    private static readonly Lazy<Types> s_types = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record Types(Type Annotation, Type Location);

    /// <summary>
    /// Creates the annotation. Only non-<see langword="null"/> values are written, so a property
    /// that does not exist in the referenced HotChocolate version is simply never touched — except
    /// <paramref name="location"/>, which is required and always set.
    /// </summary>
    public static IResourceAnnotation Create(
        SourceSchemaLocation location,
        string? sourceSchemaName = null,
        string? endpointName = null,
        string? schemaPath = null,
        string? graphQLPath = null)
    {
        var types = s_types.Value;

        // GraphQLSourceSchemaAnnotation declares `required SourceSchemaLocationType Location`, and its
        // implicit constructor carries no [SetsRequiredMembers]. Activator.CreateInstance refuses such
        // a type outright (MissingMethodException), so allocate without running a constructor: the type
        // is a sealed class of auto-properties with no field initializers, which makes this safe.
        var annotation = RuntimeHelpers.GetUninitializedObject(types.Annotation);

        // Mapped by name rather than by ordinal — a reordered enum would silently change meaning.
        SetRequired(types, annotation, "Location", Enum.Parse(types.Location, location.ToString()));

        SetOptional(types, annotation, "SourceSchemaName", sourceSchemaName);
        SetOptional(types, annotation, "EndpointName", endpointName);
        SetOptional(types, annotation, "SchemaPath", schemaPath);
        SetOptional(types, annotation, "GraphQLPath", graphQLPath);

        return (IResourceAnnotation)annotation;
    }

    private static void SetRequired(Types types, object target, string propertyName, object value)
        => (FindProperty(types.Annotation, propertyName)
            ?? throw new InvalidOperationException(
                $"'{AnnotationTypeName}.{propertyName}' was not found. The referenced "
                + $"HotChocolate.Fusion.Aspire ({types.Annotation.Assembly.GetName().Version}) is not "
                + "compatible with this integration."))
           .SetValue(target, value);

    private static void SetOptional(Types types, object target, string propertyName, string? value)
    {
        if (value is null)
        {
            return;
        }

        SetRequired(types, target, propertyName, value);
    }

    // `init` accessors are ordinary setters carrying a modreq, so SetValue works on them.
    private static PropertyInfo? FindProperty(Type type, string name)
        => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static Types Resolve()
    {
        // Anchored on a public type from the same assembly rather than Assembly.Load(string): this is
        // checked at compile time and cannot miss because the assembly has not been loaded yet.
        var assembly = typeof(GraphQLResourceBuilderExtensions).Assembly;

        return new Types(
            GetType(assembly, AnnotationTypeName),
            GetType(assembly, LocationTypeName));
    }

    private static Type GetType(Assembly assembly, string typeName)
        => assembly.GetType(typeName, throwOnError: false)
           ?? throw new InvalidOperationException(
               $"Type '{typeName}' was not found in {assembly.GetName().Name} "
               + $"{assembly.GetName().Version}. This integration reaches into HotChocolate internals "
               + "and needs updating for this version.");
}
