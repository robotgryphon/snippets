# External GraphQL source schemas — making an Aspire `ExternalServiceResource` visible to Fusion

## Problem

HotChocolate's Aspire integration already has the discovery mechanism we need. `Fusion.Aspire`
finds source schemas by scanning the app model for a marker interface plus an annotation:

```csharp
// GraphQLResourceBuilderExtensions.cs
internal static IEnumerable<IResourceWithEndpoints> GetGraphQLSchemaResources(
    this DistributedApplicationModel appModel)
    => appModel.Resources.OfType<IResourceWithEndpoints>().Where(r => r.HasGraphQLSchema());
```

`HasGraphQLSchema()` is `Annotations.OfType<GraphQLSourceSchemaAnnotation>().Any()`. Nothing else
qualifies a resource. So a subgraph is exactly: *an `IResourceWithEndpoints` carrying a
`GraphQLSourceSchemaAnnotation`*.

Aspire's external service is neither:

```csharp
// Aspire.Hosting/ExternalServiceResource.cs
public sealed class ExternalServiceResource : Resource
{
    public Uri? Uri { get; }                        // null when the URL is parameterized
    public ParameterResource? UrlParameter { get; } // null when a literal URI was supplied
}
```

It does not implement `IResourceWithEndpoints`, and it is `sealed`, so we cannot subclass it into
compliance. A GraphQL API we don't own is invisible to composition, and Fusion silently produces a
gateway missing that subgraph.

The fix is a **new resource type that implements `IResourceWithEndpoints` and holds the external
service as a reference**, then carries the source-schema annotation on itself.

## The resource

`IResourceWithEndpoints` is an empty marker interface — implementing it is free. All the work is in
producing an endpoint that satisfies HotChocolate's lookup.

```csharp
public sealed class ExternalGraphQLResource(string name, ExternalServiceResource service)
    : Resource(name), IResourceWithEndpoints, IResourceWithoutLifetime
{
    public ExternalServiceResource Service { get; } = service;
}
```

`IResourceWithoutLifetime` is load-bearing, not decoration: it marks the resource as a holder of data
rather than something to launch, which is how we expect to keep DCP from trying to allocate or proxy
the endpoint we are about to synthesize by hand. See *Risks*.

A reference, not `IResourceWithParent`. The external service keeps its own identity and its own
`WithReference` service-discovery behaviour; this resource is a **projection** of it into Fusion's
world, and more than one can exist per host (`/graphql` and `/admin/graphql` are two source schemas
behind one URL). A `ResourceRelationshipAnnotation` of type `Reference` gives the dashboard the link
without claiming ownership.

## What HotChocolate actually reads

```csharp
internal static string? GetGraphQLSchemaUrl(this IResourceWithEndpoints resource, string path)
{
    var annotation = resource.Annotations.OfType<GraphQLSourceSchemaAnnotation>().FirstOrDefault();
    if (annotation is not { Location: SourceSchemaLocationType.SchemaEndpoint }) return null;

    var endpoint = resource.GetEndpoints().FirstOrDefault(e => e.EndpointName == annotation.EndpointName);
    if (endpoint?.Url == null) return null;

    return endpoint.Url.TrimEnd('/') + path;
}
```

`GetAllocatedHttpEndpointUrl` additionally requires `endpoint is { IsAllocated: true }`. So the
contract we must satisfy is precisely: **an `EndpointAnnotation` whose name matches the annotation's
`EndpointName` (default `"http"`), with `AllocatedEndpoint` set.**

## Attaching the annotation

`GraphQLSourceSchemaAnnotation` is `internal`, with `InternalsVisibleTo` only for HotChocolate's own
tests:

```csharp
internal sealed class GraphQLSourceSchemaAnnotation : IResourceAnnotation
{
    public string? SourceSchemaName { get; init; }
    public string? EndpointName { get; init; }
    public string? SchemaPath { get; init; }
    public string? GraphQLPath { get; init; }
    public required SourceSchemaLocationType Location { get; init; }
}
```

The public extension methods each hardcode `Location`: `WithGraphQLHttpEndpoint` and
`WithGraphQLSchemaEndpoint` always write `SchemaEndpoint`, and `WithGraphQLSchemaFile` — the only
route to `ProjectDirectory` — is `[Obsolete]` and exposes neither `GraphQLPath` nor `EndpointName`.
There is no public way to set `Location` independently of the path and endpoint values, so the
annotation is constructed by **reflection**.

Two details make this less fragile than it sounds:

- **The assembly is anchored on a public type**, `typeof(GraphQLResourceBuilderExtensions).Assembly`,
  rather than `Assembly.Load("HotChocolate.Fusion.Aspire")`. That is checked at compile time and
  cannot fail because the assembly has not been loaded yet.
- **The enum is mapped by name**, not by ordinal. `SourceSchemaLocationType` is internal too, so
  values are resolved with `Enum.Parse(locationType, "ProjectDirectory")` — a reordered enum breaks
  loudly instead of silently changing meaning.

`Location` is `required`, so its implicit constructor carries no `[SetsRequiredMembers]` and
`Activator.CreateInstance` refuses the type with a `MissingMethodException`. The factory allocates
with `RuntimeHelpers.GetUninitializedObject` instead, which is safe here: the annotation is a sealed
class of auto-properties with no field initializers and no constructor logic. `init` accessors are
ordinary setters carrying a modreq, so `PropertyInfo.SetValue` writes them without complaint.

Only non-null values are written, so a property missing from a given HotChocolate version is never
touched; `Location` is always set and throws a version-diagnostic message if absent. The annotation
is added through `IResource.Annotations`, typed as the public `IResourceAnnotation`, so the internal
type is never named in a signature.

```csharp
var catalog = builder.AddExternalService("catalog", "https://catalog.contoso.com");

builder.AddExternalGraphQL("catalog-graphql", catalog)
       .WithGraphQLSourceSchema(
           location: SourceSchemaLocation.ProjectDirectory,
           schemaPath: "catalog.graphqls",
           graphQLPath: "/graphql");

builder.AddProject<Projects.Gateway>("gateway").WithNitroComposition();
```

## Synthesizing the endpoint

```csharp
var ep = new EndpointAnnotation(
    protocol: ProtocolType.Tcp,
    uriScheme: uri.Scheme,
    name: "http",
    port: uri.Port,
    isExternal: true,
    isProxied: false);

ep.AllocatedEndpoint = new AllocatedEndpoint(ep, uri.Host, uri.Port);
```

Two consequences fall out of `AllocatedEndpoint`, and both shape the API:

```csharp
public string UriString => $"{UriScheme}://{Address}:{Port}";
```

- **No path component exists.** An external service at `https://api.contoso.com/catalog` cannot be
  represented — `UriString` drops `/catalog`, and HotChocolate then appends its own `path`, yielding
  `https://api.contoso.com:443/graphql`. Silently wrong. `AddExternalGraphQL` must therefore read
  `Uri.AbsolutePath` and **prefix it onto the `path` and `schemaPath`** passed to
  `WithGraphQLHttpEndpoint`, or reject a base path outright. Prefixing automatically is the plan; a
  caller who also passes a path should get a composed result, not a surprise.
- **The port is always explicit.** `https://catalog.contoso.com` becomes
  `https://catalog.contoso.com:443`. Legal, but the `Host` header now carries `:443`, which some
  vhost routers, CDNs, and certificate setups treat as a different host. Needs a real-service test
  before we call this done.

## Parameterized URLs

When the external service was built from a `ParameterResource`, `Uri` is `null` and the value must be
read asynchronously, so the endpoint cannot be allocated while building the model. The
`EndpointAnnotation` is added eagerly (so the resource always looks well-formed to a scan) and
`AllocatedEndpoint` is assigned during eventing, before composition runs.

That makes **ordering** the open question: composition must not read the endpoint before we have
filled it. If our subscriber cannot be guaranteed to run first, `AddExternalGraphQL` should require a
literal `Uri` in v1 and reject `UrlParameter` with a clear message rather than race.

## Risks

1. **DCP and a hand-allocated endpoint.** The whole design rests on DCP leaving a resource with an
   `EndpointAnnotation` alone when it has no container, project, or executable to launch.
   `isProxied: false` plus `IResourceWithoutLifetime` is the intended defence. Unverified — this is
   the first thing to spike, because if DCP tries to allocate a port, the approach needs rethinking.
2. **Composition ordering** versus async URL resolution, above.
3. **`Host` header carrying an explicit default port.**
4. **Internal-type coupling.** Real, and the cost of setting `Location` freely. A HotChocolate
   upgrade that renames the annotation, its properties, or the enum values breaks the factory — by
   design it fails loudly at startup with a version-naming message rather than silently producing an
   annotation Fusion ignores. Pin `HotChocolate.Fusion.Aspire` and treat its upgrades as a change
   that needs a test run.

## Milestones

- **M0** — spike risk 1 in a throwaway AppHost: does a hand-allocated endpoint on a lifetime-less
  resource survive startup, and does composition see it?
- **M1** — `ExternalGraphQLResource`, `AddExternalGraphQL`, literal-`Uri` only, base-path folding.
  Success is a composed gateway containing a subgraph that is not a project.
- **M2** — `UrlParameter` support with resolution ordered ahead of composition.
- **M3** — dashboard relationship and a GraphQL-aware health probe (`POST { __typename }`; a `GET /`
  on a GraphQL host proves nothing and commonly 404s).

## Verified against

- `ChilliCream/graphql-platform` @ `443e680` — `src/HotChocolate/Fusion/src/Fusion.Aspire/`.
- `HotChocolate.Fusion.Aspire` 16.6.1 (shipped) for the public API assertion.
- `dotnet/aspire` `main` — `ExternalServiceResource.cs`, `EndpointAnnotation.cs`,
  `AllocatedEndpoint.cs`, `EndpointReference.cs`, `IResourceWithEndpoints.cs`.

Note `AddGraphQLOrchestrator()` is `[Obsolete]` in favour of `AddNitroComposition()`.
