using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ExternalGraphQL;

/// <summary>
/// Projects an <see cref="ExternalServiceResource"/> into something HotChocolate's Fusion
/// integration can discover.
/// </summary>
/// <remarks>
/// Fusion finds source schemas with
/// <c>appModel.Resources.OfType&lt;IResourceWithEndpoints&gt;().Where(r =&gt; r.HasGraphQLSchema())</c>.
/// <see cref="ExternalServiceResource"/> implements neither that interface nor allows subclassing
/// (it is sealed), so it can never be found. This resource implements the marker and holds the
/// external service by reference.
/// <para>
/// <see cref="IResourceWithoutLifetime"/> is deliberate: there is nothing to launch, and it keeps
/// the orchestrator from trying to allocate the endpoint we assign by hand.
/// </para>
/// </remarks>
public sealed class ExternalGraphQLResource : Resource, IResourceWithEndpoints, IResourceWithoutLifetime
{
    public ExternalGraphQLResource(string name, ExternalServiceResource service)
        : base(name)
        => Service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>The external service this resource projects.</summary>
    public ExternalServiceResource Service { get; }
}
