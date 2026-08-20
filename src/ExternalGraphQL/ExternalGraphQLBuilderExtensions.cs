using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ExternalGraphQL;

public static class ExternalGraphQLBuilderExtensions
{
    private const string DefaultEndpointName = "http";

    /// <summary>
    /// Adds an <see cref="ExternalGraphQLResource"/> projecting <paramref name="service"/>, with a
    /// synthesized endpoint resolved from the external service's URI.
    /// </summary>
    /// <remarks>
    /// Only a literal <see cref="ExternalServiceResource.Uri"/> is supported. A parameterized URL
    /// resolves asynchronously and so cannot be allocated while the model is being built; it is
    /// rejected here rather than racing composition.
    /// </remarks>
    public static IResourceBuilder<ExternalGraphQLResource> AddExternalGraphQL(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ExternalServiceResource> service,
        string endpointName = DefaultEndpointName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var uri = service.Resource.Uri
            ?? throw new ArgumentException(
                $"External service '{service.Resource.Name}' has a parameterized URL. Its value is "
                + "only available asynchronously, so the endpoint cannot be allocated here. Use an "
                + "external service created from an absolute URI.",
                nameof(service));

        var resource = new ExternalGraphQLResource(name, service.Resource);

        var endpoint = new EndpointAnnotation(
            protocol: ProtocolType.Tcp,
            uriScheme: uri.Scheme,
            name: endpointName,
            port: uri.Port,
            isExternal: true,
            isProxied: false);

        // Nothing allocates this endpoint for us — the resource has no lifetime — so assign it
        // directly. AllocatedEndpoint renders as "{scheme}://{address}:{port}" and has no path
        // component; see WithGraphQLSourceSchema for how the URI's base path is preserved.
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, uri.Host, uri.Port);

        return builder.AddResource(resource)
            .WithAnnotation(endpoint)
            .WithRelationship(service.Resource, "Reference");
    }

    /// <summary>
    /// Marks the resource as a Fusion source schema, setting the location explicitly.
    /// </summary>
    /// <param name="location">
    /// Where the schema document comes from. Set independently of the endpoint and path values,
    /// which the public HotChocolate extensions do not permit.
    /// </param>
    /// <param name="schemaPath">
    /// For <see cref="SourceSchemaLocation.SchemaEndpoint"/>, the path the schema is downloaded
    /// from. For <see cref="SourceSchemaLocation.ProjectDirectory"/>, the schema file name.
    /// </param>
    /// <param name="graphQLPath">The path the GraphQL endpoint is served from.</param>
    /// <param name="sourceSchemaName">
    /// Source schema name. When omitted, HotChocolate falls back to its own default.
    /// </param>
    public static IResourceBuilder<ExternalGraphQLResource> WithGraphQLSourceSchema(
        this IResourceBuilder<ExternalGraphQLResource> builder,
        SourceSchemaLocation location,
        string? schemaPath = null,
        string? graphQLPath = "/graphql",
        string? sourceSchemaName = null,
        string endpointName = DefaultEndpointName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The allocated endpoint cannot carry a path, and HotChocolate builds its URL as
        // endpoint.Url.TrimEnd('/') + path. A base path on the external URI ("https://host/catalog")
        // would otherwise be dropped, so fold it back in here.
        var basePath = builder.Resource.Service.Uri?.AbsolutePath.TrimEnd('/');

        var annotation = GraphQLSourceSchemaAnnotationFactory.Create(
            location: location,
            sourceSchemaName: sourceSchemaName,
            endpointName: endpointName,
            schemaPath: Combine(basePath, schemaPath, location),
            graphQLPath: Combine(basePath, graphQLPath, location));

        builder.Resource.Annotations.Add(annotation);
        return builder;
    }

    // A ProjectDirectory schemaPath is a file name, not a URL path, so it is never prefixed.
    private static string? Combine(string? basePath, string? path, SourceSchemaLocation location)
        => location is SourceSchemaLocation.SchemaEndpoint && !string.IsNullOrEmpty(basePath) && path is not null
            ? basePath + path
            : path;
}
