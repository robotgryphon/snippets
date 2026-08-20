using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ExternalGraphQL;

public static class ExternalGraphQLBuilderExtensions
{
    private const string DefaultEndpointName = "http";
    private const string DefaultExternalRoot = "external";
    private const string SchemaFileName = "schema.graphqls";

    /// <summary>
    /// Adds an <see cref="ExternalGraphQLResource"/> projecting <paramref name="service"/>, backed by
    /// a synthetic project directory at <c>{externalRoot}/{name}/</c>.
    /// </summary>
    /// <param name="externalRoot">
    /// Root for synthetic project directories, relative to the AppHost directory unless absolute.
    /// </param>
    public static IResourceBuilder<ExternalGraphQLResource> AddExternalGraphQL(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ExternalServiceResource> service,
        string externalRoot = DefaultExternalRoot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var projectDirectory = Path.Combine(
            Path.IsPathRooted(externalRoot) ? externalRoot : Path.Combine(builder.AppHostDirectory, externalRoot),
            name);

        var resource = new ExternalGraphQLResource(name, service.Resource, projectDirectory);
        var projectPath = Path.Combine(projectDirectory, $"{name}.csproj");

        WriteProjectFileIfMissing(projectDirectory, projectPath);

        return builder.AddResource(resource)
            // Fusion reads the directory back out of project metadata.
            .WithAnnotation(new ExternalProjectMetadata(projectPath))
            // Load-bearing. Deriving from ProjectResource puts this in the set the orchestrator
            // builds and launches, and it does try: IResourceWithoutLifetime is not honoured for a
            // ProjectResource subclass. WithExplicitStart leaves the resource in the model — so
            // composition still finds it — while never starting it with the app host.
            .WithExplicitStart()
            // Nothing to look at: it never runs, and a permanently NotStarted row is just noise.
            .WithHidden()
            .WithRelationship(service.Resource, "Reference")
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Downloads the external service's schema on startup and registers the resource as a
    /// <see cref="SourceSchemaLocation.ProjectDirectory"/> source schema pointing at it.
    /// </summary>
    /// <param name="schemaDownloadPath">
    /// Path the SDL is fetched from, relative to the external service's URL. Fusion's own fetcher is
    /// a plain HTTP GET returning schema text, and this matches it.
    /// </param>
    /// <param name="graphQLPath">Path the GraphQL endpoint is served from at runtime.</param>
    /// <param name="sourceSchemaName">
    /// Source schema name. Defaults to the resource name, and is written into the generated
    /// settings file, which Fusion requires to agree with this value.
    /// </param>
    public static IResourceBuilder<ExternalGraphQLResource> WithDownloadedGraphQLSchema(
        this IResourceBuilder<ExternalGraphQLResource> builder,
        string schemaDownloadPath = "/graphql?sdl",
        string graphQLPath = "/graphql",
        string? sourceSchemaName = null,
        string endpointName = DefaultEndpointName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var schemaName = sourceSchemaName ?? builder.Resource.Name;

        ExternalGraphQLSchemaDownloader.Subscribe(
            builder.ApplicationBuilder,
            builder.Resource,
            endpointName,
            schemaDownloadPath,
            schemaName);

        return builder.WithGraphQLSourceSchema(
            location: SourceSchemaLocation.ProjectDirectory,
            // A ProjectDirectory SchemaPath is a file name resolved inside the project directory,
            // not a URL path.
            schemaPath: SchemaFileName,
            graphQLPath: graphQLPath,
            sourceSchemaName: schemaName,
            endpointName: endpointName);
    }

    /// <summary>
    /// Marks the resource as a Fusion source schema, setting the location explicitly.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="WithDownloadedGraphQLSchema"/>. This is the lower-level form, for a schema
    /// already on disk or a deliberate <see cref="SourceSchemaLocation.SchemaEndpoint"/>.
    /// </remarks>
    public static IResourceBuilder<ExternalGraphQLResource> WithGraphQLSourceSchema(
        this IResourceBuilder<ExternalGraphQLResource> builder,
        SourceSchemaLocation location,
        string? schemaPath = null,
        string? graphQLPath = "/graphql",
        string? sourceSchemaName = null,
        string endpointName = DefaultEndpointName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The allocated endpoint carries no path, and Fusion builds URLs as
        // endpoint.Url.TrimEnd('/') + path, so a base path on the external URI would be dropped.
        // Only URL-shaped values get it folded back in.
        var basePath = builder.Resource.Service.Uri?.AbsolutePath.TrimEnd('/');

        var annotation = GraphQLSourceSchemaAnnotationFactory.Create(
            location: location,
            sourceSchemaName: sourceSchemaName,
            endpointName: endpointName,
            schemaPath: location is SourceSchemaLocation.SchemaEndpoint
                ? Prefix(basePath, schemaPath)
                : schemaPath,
            graphQLPath: Prefix(basePath, graphQLPath));

        builder.Resource.Annotations.Add(annotation);
        return builder;
    }

    /// <summary>
    /// Writes a placeholder project file so nothing that inspects the path finds it missing.
    /// </summary>
    /// <remarks>
    /// Fusion only takes <c>Path.GetDirectoryName</c> of it, but the resource is a
    /// <see cref="ProjectResource"/> and the orchestrator prepares one before honouring explicit
    /// start. An empty SDK project is the cheapest thing that survives being looked at; a real build
    /// is suppressed by <see cref="ExternalProjectMetadata.SuppressBuild"/>.
    /// </remarks>
    private static void WriteProjectFileIfMissing(string projectDirectory, string projectPath)
    {
        Directory.CreateDirectory(projectDirectory);

        if (File.Exists(projectPath))
        {
            return;
        }

        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Generated by ExternalGraphQL. Placeholder so the resource has a project path. -->
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
            </Project>

            """);
    }

    private static string? Prefix(string? basePath, string? path)
        => string.IsNullOrEmpty(basePath) || path is null ? path : basePath + path;
}
