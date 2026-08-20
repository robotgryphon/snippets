using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ExternalGraphQL;

/// <summary>
/// Projects an <see cref="ExternalServiceResource"/> into something HotChocolate's Fusion
/// integration can discover and read a schema for.
/// </summary>
/// <remarks>
/// This derives from <see cref="ProjectResource"/> out of necessity, not because it is a project.
/// Fusion's <c>SchemaComposition.GetProjectPath</c> opens with
/// <c>if (resource is not ProjectResource) return null;</c>, and every source schema — file-based
/// <em>and</em> endpoint-based — is resolved through it, because both need a settings JSON read from
/// the project directory. A resource that merely implements <see cref="IResourceWithEndpoints"/> is
/// found by the scan and then dropped with "Could not determine project path".
/// <para>
/// <see cref="IResourceWithoutLifetime"/> and a <see cref="ExternalProjectMetadata.SuppressBuild"/>
/// of <see langword="true"/> are what keep the orchestrator from trying to build or launch the
/// synthetic project directory this resource points at.
/// </para>
/// </remarks>
public sealed class ExternalGraphQLResource : ProjectResource, IResourceWithoutLifetime
{
    public ExternalGraphQLResource(string name, ExternalServiceResource service, string projectDirectory)
        : base(name)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        ProjectDirectory = projectDirectory ?? throw new ArgumentNullException(nameof(projectDirectory));
    }

    /// <summary>The external service this resource projects.</summary>
    public ExternalServiceResource Service { get; }

    /// <summary>
    /// Directory the downloaded schema and its settings are written to, and the directory Fusion
    /// reads them back from.
    /// </summary>
    public string ProjectDirectory { get; }
}
