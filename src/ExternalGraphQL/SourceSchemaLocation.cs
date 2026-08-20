namespace ExternalGraphQL;

/// <summary>
/// Mirrors HotChocolate's internal <c>SourceSchemaLocationType</c>. Kept as our own public enum so
/// callers never touch the internal type; the factory maps it by <em>name</em>, not by ordinal.
/// </summary>
public enum SourceSchemaLocation
{
    /// <summary>The schema document is read from the resource's project directory.</summary>
    ProjectDirectory,

    /// <summary>The schema document is downloaded from the resource's endpoint.</summary>
    SchemaEndpoint
}
