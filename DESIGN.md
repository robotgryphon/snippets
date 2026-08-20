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

The scan is on `IResourceWithEndpoints`, but that alone is not enough to be *usable*. Fusion resolves
every source schema's files through `SchemaComposition.GetProjectPath`, which opens with:

```csharp
private string? GetProjectPath(IResourceWithEndpoints resource)
{
    if (resource is not ProjectResource projectResource)
    {
        return null;
    }
    ...
}
```

A `null` there ends the resource's participation: `ReadSchemaFromProjectDirectoryAsync` logs
"Could not determine project path" and returns null, and so does `GetSourceSchemaSettingsAsync`. This
applies to **both** locations — `SchemaEndpoint` also reads a `schema-settings.json` from the project
directory — so no custom resource can supply a source schema unless it *is* a `ProjectResource`.

`ProjectResource` is not sealed, so the resource derives from it:

```csharp
public sealed class ExternalGraphQLResource : ProjectResource, IResourceWithoutLifetime
{
    public ExternalServiceResource Service { get; }
    public string ProjectDirectory { get; }
}
```

It carries an `IProjectMetadata` annotation whose `ProjectPath` is
`{externalRoot}/{name}/{name}.csproj`. Fusion only ever takes `Path.GetDirectoryName` of that, so the
project file never has to exist — but Aspire will otherwise try to build and launch it, hence
`SuppressBuild => true`, `IResourceWithoutLifetime`, and `ExcludeFromManifest()`. See *Risks*.

A reference, not `IResourceWithParent`. The external service keeps its own identity and its own
`WithReference` service-discovery behaviour; this resource is a **projection** of it into Fusion's
world, and more than one can exist per host (`/graphql` and `/admin/graphql` are two source schemas
behind one URL). A `ResourceRelationshipAnnotation` of type `Reference` gives the dashboard the link
without claiming ownership.

## Downloading the schema

`ProjectDirectory` is the wanted location because the `SchemaEndpoint` branch populates
`SourceSchemaInfo` with settings we do not want — `HttpEndpointUrl` and an endpoint configuration
read through `ReadEndpointConfiguration`. The file branch leaves `HttpEndpointUrl` null and keeps
`AllocatedHttpEndpointUrl` as the only runtime URL. So the schema is fetched by us and handed to
Fusion as a file.

A `BeforeStartEvent` subscriber, registered while the model is built, does three things:

1. **Resolves the external URL**, awaiting `UrlParameter` through `IValueProvider` when there is no
   literal `Uri`. Being in an async handler is what makes parameterized URLs work at all.
2. **Allocates the endpoint** from the resolved URL. Nothing else will: the resource has no lifetime,
   so no orchestrator assigns a port, and `GetAllocatedHttpEndpointUrl` returns null without
   `IsAllocated`.
3. **Downloads and writes** `{externalRoot}/{name}/schema.graphqls`, plus `schema-settings.json` if
   absent — Fusion derives that name as `{fileNameWithoutExtension}-settings.json` and requires a
   non-empty `name` in it that agrees with the annotation's `SourceSchemaName`. An existing settings
   file is left alone so hand-authored settings survive.

`BeforeStartEvent` is forced, not chosen. Fusion composes from its own `BeforeStartEvent` subscriber,
and `WaitForSourceSchemaResourcesReadyAsync` skips anything that is not `SchemaEndpoint` — a file
source is read straight off disk with no wait. The file must therefore exist before Fusion's handler
runs, which rules out `InitializeResourceEvent` and everything after it. Ordering holds because our
subscription is registered during model building while Fusion subscribes from
`IDistributedApplicationEventingSubscriber.SubscribeAsync` at startup, and subscribers are dispatched
in registration order.

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
read asynchronously, so nothing about the endpoint can be settled while the model is built. Both the
`EndpointAnnotation` and its `AllocatedEndpoint` are therefore created in the download hook, once the
URL has resolved — the same handler, for the same reason, so there is one ordering dependency rather
than two.

The cost is that the resource carries no endpoint annotation until `BeforeStartEvent`. Nothing in
Fusion looks before then, but any other code that enumerates endpoints during model building will see
none.

## Risks

1. **A `ProjectResource` that must not be launched.** *Confirmed and handled.* Deriving from
   `ProjectResource` is forced by `GetProjectPath`, and it does put the resource in the set the
   orchestrator launches — `IResourceWithoutLifetime` is **not** honoured for a `ProjectResource`
   subclass, so that marker has been dropped rather than left as decoration. The fix is
   `WithExplicitStart()`, which keeps the resource in the model where composition finds it but never
   starts it with the app host, plus `WithHidden()` to keep a permanently NotStarted row out of the
   dashboard, `SuppressBuild`, and a generated placeholder `.csproj` so nothing that inspects the
   path finds it missing. The clean long-term fix is still upstream: a `GetProjectPath` that accepts
   any resource carrying `IProjectMetadata` instead of type-checking `ProjectResource`.
2. **Subscriber ordering.** Our download must precede Fusion's composition within the same
   `BeforeStartEvent`. Registration order gives us that today; it is a behavioural dependency, not a
   contract, and a dispatch that ever went concurrent would break it silently — the symptom would be
   "Schema file not found" on a cold start and success on the next.
3. **`Host` header carrying an explicit default port.**
4. **Internal-type coupling.** Real, and the cost of setting `Location` freely. A HotChocolate
   upgrade that renames the annotation, its properties, or the enum values breaks the factory — by
   design it fails loudly at startup with a version-naming message rather than silently producing an
   annotation Fusion ignores. Pin `HotChocolate.Fusion.Aspire` and treat its upgrades as a change
   that needs a test run.

## Milestones

- **M0** — spike risk 1 in a throwaway AppHost: does a lifetime-less `ProjectResource` subclass
  survive startup without being built or launched, and does composition see its schema?
- **M1** — `ExternalGraphQLResource`, `AddExternalGraphQL`, `WithDownloadedGraphQLSchema`. Success is
  a composed gateway containing a source schema that is not a project.
- **M2** — recomposition when the external schema changes; today the schema is fetched once at
  startup.
- **M3** — dashboard relationship and a GraphQL-aware health probe (`POST { __typename }`; a `GET /`
  on a GraphQL host proves nothing and commonly 404s).

## Verified against

- `ChilliCream/graphql-platform` @ `443e680` — `src/HotChocolate/Fusion/src/Fusion.Aspire/`.
- `HotChocolate.Fusion.Aspire` 16.6.1 (shipped) for the public API assertion.
- `dotnet/aspire` `main` — `ExternalServiceResource.cs`, `EndpointAnnotation.cs`,
  `AllocatedEndpoint.cs`, `EndpointReference.cs`, `IResourceWithEndpoints.cs`.

Note `AddGraphQLOrchestrator()` is `[Obsolete]` in favour of `AddNitroComposition()`.
