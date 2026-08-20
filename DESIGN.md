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

`GraphQLSourceSchemaAnnotation` is `internal` with `InternalsVisibleTo` only for HotChocolate's own
tests — but we never need to name it. The method that creates it is public and generic over exactly
the interface we now implement:

```csharp
[AspireExport]
public static IResourceBuilder<T> WithGraphQLHttpEndpoint<T>(
    this IResourceBuilder<T> builder,
    string path = "/graphql",
    string? schemaPath = "/graphql/schema.graphql",
    string endpointName = "http",
    string? sourceSchemaName = null)
    where T : IResourceWithEndpoints
```

Verified present in the shipped `HotChocolate.Fusion.Aspire` **16.6.1** for `net9.0`, `net10.0` and
`net11.0`. The generic constraint is the entire contract, and satisfying it is the whole point of the
new resource type — so the intended call site is:

```csharp
var catalog = builder.AddExternalService("catalog", "https://catalog.contoso.com");

builder.AddExternalGraphQL("catalog-graphql", catalog)
       .WithGraphQLHttpEndpoint(path: "/graphql", schemaPath: "/graphql/schema.graphql");

builder.AddProject<Projects.Gateway>("gateway").WithNitroComposition();
```

Nothing here is reflection, and nothing depends on an internal type. `WithGraphQLSchemaFile` and
`WithGraphQLSchemaEndpoint` are also public but both carry `[Obsolete]` — file-based source schemas
are being retired in favour of fetching from the endpoint.

**Reflection fallback.** Only needed on a version predating `WithGraphQLHttpEndpoint`, or to set a
field the public method does not expose (there is none today — it covers all five annotation
properties). If it ever is: resolve the type by name from the `HotChocolate.Fusion.Aspire` assembly,
construct via `Activator.CreateInstance` with an object initializer through property setters (all
`init`), and add with `builder.WithAnnotation(...)` typed as `IResourceAnnotation`. Guard it behind a
single adapter with a version probe so the supported path is used whenever it exists — this is a
fragile fallback, not the design.

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
4. **Internal-type coupling.** Zero at compile time on the supported path. The coupling that remains
   is behavioural: we depend on HotChocolate looking up an endpoint *by name* with `IsAllocated`.

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
