using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExternalGraphQL;

/// <summary>
/// Resolves the external service's URL, downloads its schema into the resource's project directory,
/// and allocates the endpoint Fusion reads the runtime URL from.
/// </summary>
internal static class ExternalGraphQLSchemaDownloader
{
    private const string SchemaFileName = "schema.graphqls";

    /// <summary>
    /// Runs the download on <see cref="BeforeStartEvent"/>.
    /// </summary>
    /// <remarks>
    /// The event choice is forced. Fusion composes from its own <see cref="BeforeStartEvent"/>
    /// subscriber, and it only waits for resources whose location is <c>SchemaEndpoint</c> — a
    /// <c>ProjectDirectory</c> source is read straight off disk with no wait. So the file has to
    /// exist before Fusion's handler runs, which rules out <c>InitializeResourceEvent</c> and
    /// anything later.
    /// <para>
    /// Ordering holds because this subscribes while the model is being built, whereas Fusion
    /// subscribes from <c>IDistributedApplicationEventingSubscriber.SubscribeAsync</c> at startup,
    /// and <see cref="BeforeStartEvent"/> subscribers are dispatched in registration order.
    /// </para>
    /// </remarks>
    public static void Subscribe(
        IDistributedApplicationBuilder builder,
        ExternalGraphQLResource resource,
        string endpointName,
        string schemaDownloadPath,
        string sourceSchemaName)
        => builder.Eventing.Subscribe<BeforeStartEvent>(
            (@event, cancellationToken) => RunAsync(
                @event,
                resource,
                endpointName,
                schemaDownloadPath,
                sourceSchemaName,
                cancellationToken));

    private static async Task RunAsync(
        BeforeStartEvent @event,
        ExternalGraphQLResource resource,
        string endpointName,
        string schemaDownloadPath,
        string sourceSchemaName,
        CancellationToken cancellationToken)
    {
        var logger = @event.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger($"ExternalGraphQL.{resource.Name}");

        var baseUri = await ResolveBaseUriAsync(resource.Service, cancellationToken);

        // Allocated here rather than at model-build time: a parameterized URL only resolves
        // asynchronously, and Fusion does not read the endpoint until it composes.
        AllocateEndpoint(resource, endpointName, baseUri);

        var schemaUri = ExternalGraphQLUri.Resolve(baseUri, schemaDownloadPath);

        logger.LogInformation(
            "Downloading GraphQL schema for {ResourceName} from {SchemaUri}",
            resource.Name,
            schemaUri);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var schema = await httpClient.GetStringAsync(schemaUri, cancellationToken);

        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new DistributedApplicationException(
                $"The schema downloaded for '{resource.Name}' from {schemaUri} was empty.");
        }

        Directory.CreateDirectory(resource.ProjectDirectory);

        var schemaFile = Path.Combine(resource.ProjectDirectory, SchemaFileName);
        await File.WriteAllTextAsync(schemaFile, schema, cancellationToken);

        await WriteSettingsIfMissingAsync(resource, sourceSchemaName, logger, cancellationToken);

        logger.LogInformation("Wrote schema for {ResourceName} to {SchemaFile}", resource.Name, schemaFile);
    }

    private static async Task<Uri> ResolveBaseUriAsync(
        ExternalServiceResource service,
        CancellationToken cancellationToken)
    {
        if (service.Uri is { } uri)
        {
            return uri;
        }

        if (service.UrlParameter is not { } parameter)
        {
            throw new DistributedApplicationException(
                $"External service '{service.Name}' has neither a URI nor a URL parameter.");
        }

        var value = await ((IValueProvider)parameter).GetValueAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var resolved))
        {
            throw new DistributedApplicationException(
                $"Parameter '{parameter.Name}' for external service '{service.Name}' did not resolve "
                + $"to an absolute URL (got '{value}').");
        }

        return resolved;
    }

    private static void AllocateEndpoint(ExternalGraphQLResource resource, string endpointName, Uri baseUri)
    {
        var endpoint = resource.Annotations
            .OfType<EndpointAnnotation>()
            .FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparison.Ordinal));

        if (endpoint is null)
        {
            endpoint = new EndpointAnnotation(
                protocol: ProtocolType.Tcp,
                uriScheme: baseUri.Scheme,
                name: endpointName,
                port: baseUri.Port,
                isExternal: true,
                isProxied: false);

            resource.Annotations.Add(endpoint);
        }

        // Nothing allocates this for us: the resource has no lifetime, so no orchestrator assigns a
        // port. Fusion's GetAllocatedHttpEndpointUrl requires IsAllocated, and returns null without it.
        endpoint.AllocatedEndpoint ??= new AllocatedEndpoint(endpoint, baseUri.Host, baseUri.Port);
    }

    /// <summary>
    /// Writes the settings file Fusion reads alongside the schema — for <c>schema.graphqls</c> that
    /// is <c>schema-settings.json</c>, since Fusion derives it as
    /// <c>{fileNameWithoutExtension}-settings.json</c>. An existing file is left alone so
    /// hand-authored settings survive.
    /// </summary>
    private static async Task WriteSettingsIfMissingAsync(
        ExternalGraphQLResource resource,
        string sourceSchemaName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var settingsFile = Path.Combine(
            resource.ProjectDirectory,
            $"{Path.GetFileNameWithoutExtension(SchemaFileName)}-settings.json");

        if (File.Exists(settingsFile))
        {
            logger.LogDebug("Keeping existing schema settings at {SettingsFile}", settingsFile);
            return;
        }

        // Fusion requires a non-empty string "name", and rejects composition if it disagrees with
        // the annotation's SourceSchemaName.
        var settings = JsonSerializer.Serialize(
            new Dictionary<string, object> { ["name"] = sourceSchemaName },
            new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(settingsFile, settings, cancellationToken);
    }
}
