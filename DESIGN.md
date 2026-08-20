# External GraphQL Subgraphs — teaching Aspire about a HotChocolate service it doesn't own

## Problem

Fusion 16 rebuilt its Aspire story around *projects*. `builder.AddGraphQLOrchestrator()` installs a
startup hook that discovers subgraph schemas and composes them; each subgraph is an ordinary
`AddProject<…>()` resource that ships a `schema-settings.json` with an `aspire` environment naming
its local GraphQL endpoint. `AddFusionGateway` and the explicit `.Compose()` call are gone — the
gateway is a normal project that loads the composed archive.

That model has no seat for a GraphQL API **we don't build**: a subgraph owned by another team, a
vendor API, a legacy service that already runs somewhere. In the app model it is an
`ExternalServiceResource`:

```csharp
var catalog = builder.AddExternalService("catalog", "https://catalog.contoso.com");
var orders  = builder.AddExternalService("orders", builder.AddParameter("orders-url"));
```

`ExternalServiceResource` is a sealed `Resource` implementing only `IResourceWithoutLifetime`. It
carries a `Uri` **or** a `UrlParameter` (never both), no project, no `schema-settings.json`, and no
compile step we can hook. The orchestrator cannot see it, so it silently composes a gateway that is
missing those subgraphs.

We want the external service to become a first-class subgraph without pretending it is a project:

```csharp
builder.AddGraphQLOrchestrator();

var catalog = builder.AddExternalService("catalog", "https://catalog.contoso.com")
    .AsGraphQLSubgraph("catalog", sg => sg
        .WithPath("/graphql")
        .WithSchemaFile("./schemas/catalog.graphql")
        .WithHeaderPropagation("Authorization", "traceparent"));

builder.AddProject<Projects.Gateway>("gateway").WithReference(catalog);
```

## Approach

A **child resource** carrying **annotations**, not annotations sprayed onto the external service.

```csharp
public sealed class GraphQLSubgraphResource(string name, ExternalServiceResource parent)
    : Resource(name),
      IResourceWithParent<ExternalServiceResource>,
      IResourceWithoutLifetime
{
    public ExternalServiceResource Parent { get; } = parent;
    public string SchemaName => this.GetSchemaName();   // annotation-backed
}
```

`AsGraphQLSubgraph` adds this resource to the app model, parented to the external service, and the
configuration callback writes annotations onto **it**:

| Annotation | Cardinality | Carries |
| --- | --- | --- |
| `GraphQLSubgraphAnnotation` | replace | schema name as the composed graph sees it |
| `GraphQLEndpointAnnotation` | append | path + transport (`Http`, `WebSocket`, `ServerSentEvents`) |
| `GraphQLSchemaSourceAnnotation` | replace | `File`, `Introspection`, `FusionArchive`, or `Registry` |
| `GraphQLHeaderPropagationAnnotation` | append | header names forwarded gateway → subgraph |
| `GraphQLClientNameAnnotation` | replace | `HttpClient` name the gateway resolves for this subgraph |

Annotations are the transport, exactly as the rest of Aspire does it — `WithAnnotation<T>(…,
ResourceAnnotationMutationBehavior.Replace)` for single-valued facts, `Append` for lists. Nothing
reads state off the builder; every consumer reads the model.

### Why a child resource and not annotations on the external service

Annotating `ExternalServiceResource` in place is the smaller change, and it was the first sketch. It
loses on three counts:

- **Discovery.** The orchestrator wants `model.Resources.OfType<GraphQLSubgraphResource>()`. The
  in-place version forces a scan of every resource in the model asking "do you happen to have a
  GraphQL annotation?" — the same shape as a marker interface, with none of the type safety.
- **Fan-out.** One host can front more than one schema (`/graphql` and `/admin/graphql`, or a
  versioned pair). One external service → *n* subgraph children models that directly; annotations on
  the parent would need every one of them keyed by schema name by hand.
- **Dashboard.** A child resource nests under its parent and gets its own state, URLs, and health.
  That is where "catalog subgraph: schema stale" belongs, and an annotation cannot render.

The cost is one extra node in the graph and an `IResourceWithParent` hop when resolving the URL. Both
are cheap. `WithReference(catalog)` on the gateway keeps working either way — service discovery reads
the parent, and the child forwards.

### Resolving the URL

The one genuinely awkward part. `Uri` is null whenever the resource was built from a parameter, and a
`ParameterResource` value must be read **asynchronously** (`GetValueAsync`) — Aspire's own
`WithHealthCheck` for external services has a bug filed for reading it synchronously
([dotnet/aspire#10468](https://github.com/dotnet/aspire/issues/10468)). So:

- `AsGraphQLSubgraph` and the callbacks are pure model-building. They resolve nothing.
- URL resolution happens once during eventing, in an `InitializeResourceEvent` (or `BeforeStartEvent`)
  subscriber, and the resolved absolute endpoint is cached back onto the resource as an annotation
  for later stages to read.
- Missing parameter value → a diagnostic naming the parameter, not a `NullReferenceException`
  ([dotnet/aspire#10352](https://github.com/dotnet/aspire/issues/10352)).

### Schema acquisition

Composition needs SDL. Four sources, in the order we expect them to be used:

1. **`WithSchemaFile(path)`** — a checked-in `.graphql`. Deterministic, offline, reviewable in a PR;
   goes stale silently. The default recommendation, and the only one M1 ships.
2. **`WithIntrospection()`** — fetch at startup. Always current; needs the service reachable and
   introspection enabled, which production subgraphs often disable.
3. **`WithFusionArchive(path)`** — a pre-composed `.far` fragment, for a subgraph published by
   another team's pipeline.
4. **`WithSchemaRegistry(...)`** — pull by schema name + tag. Deferred; it is the right answer for a
   real deployment and the wrong first milestone.

Whichever source is used, the resolved SDL lands in a well-known per-subgraph location the
orchestrator's discovery step reads, alongside a synthesized `schema-settings.json`-shaped fragment
whose `aspire` environment points at the resolved external URL. **This is the interface we are least
sure of** — see *Verify first*.

### Readiness, without `WaitFor`

Aspire cannot `WaitFor` an external service ([dotnet/aspire#10827](https://github.com/dotnet/aspire/issues/10827)),
and `WithHttpProbe` / `WithHttpHealthCheck` don't apply because `ExternalServiceResource` doesn't
implement `IResourceWithEndpoints` ([microsoft/aspire#11428](https://github.com/microsoft/aspire/issues/11428),
[#12115](https://github.com/microsoft/aspire/issues/12115)). Under introspection, composition would
race a service that isn't up.

The subgraph resource brings its own gate: a GraphQL-aware probe that `POST`s `{ __typename }` to the
resolved endpoint and requires a `200` with no `errors`. A `GET /` on a GraphQL host proves nothing —
it commonly 404s while the schema is perfectly healthy. The probe registers as a real health check on
the child resource, so the dashboard shows it, and the composition step awaits it with bounded retry
before reading the schema. File-sourced schemas skip the gate entirely and compose offline.

## Layout

- `AspireHotChocolate/` — annotations, `GraphQLSubgraphResource`, the builder extensions. AppHost-side
  only; references `Aspire.Hosting`, no HotChocolate runtime dependency.
- `AspireHotChocolate.Composition/` — schema acquisition, the probe, and the orchestrator hand-off.
  Split out because it is the piece most likely to churn against Fusion versions.
- `samples/` — an AppHost with one external subgraph and one project subgraph behind a gateway,
  which is also the only honest integration test.

## Milestones

- **M0** — this doc, plus a spike that pins the package versions and answers *Verify first*.
- **M1** — annotations, resource, extensions, `WithSchemaFile`. A composed gateway that includes an
  external subgraph from a checked-in SDL.
- **M2** — URL resolution through `UrlParameter`, the readiness probe, `WithIntrospection()`.
- **M3** — header propagation and `HttpClient` naming on the gateway side; subscriptions transport.
- **M4** — publish/deploy manifest behavior; `WithSchemaRegistry`.

## Verify first

Written against Fusion 16's documented Aspire model and Aspire 13's `ExternalServiceResource`, but
the ChilliCream docs were not reachable while drafting. Confirm against the pinned packages before
building anything on top:

- **Does `AddGraphQLOrchestrator()` expose a discovery extension point?** If it does, M1 is an
  implementation of that interface and most of `AspireHotChocolate.Composition/` disappears. If it
  only walks project resources, we emit files it already reads — or drive `fusion compose` ourselves
  and hand the gateway a `.far`. This single answer decides the shape of M1.
- The exact `schema-settings.json` schema for the `aspire` environment, and whether a synthesized one
  is respected for a non-project resource.
- Whether `AddGraphQLGateway().AddFileSystemConfiguration("./gateway.far")` can be pointed at a
  composed archive produced at AppHost startup, or expects a build-time artifact.
- Whether `IResourceWithoutLifetime` children receive `InitializeResourceEvent` — if not, the URL
  resolution and probe both move to `BeforeStartEvent`.

## Open questions

- **Publish mode.** Under `aspire publish`, the external service is just a URL in the manifest. Does
  the subgraph child emit anything, or is composition strictly a local-orchestration concern with the
  deployed gateway getting its archive from CI? Leaning toward the latter.
- **Schema drift.** A checked-in SDL that no longer matches the live service produces a gateway that
  composes cleanly and fails at runtime. A dev-time introspect-and-diff warning would catch it; it
  needs introspection to be enabled, which is the case we already can't rely on.
- **Naming.** `AsGraphQLSubgraph` follows Aspire's `As*` convention for "project this resource as
  something else" and reads correctly when it returns a *different* builder. If it ends up returning
  `IResourceBuilder<ExternalServiceResource>` for chaining, it should be `WithGraphQLSubgraph`.
- **Fusion version coupling.** The composition hand-off is the only version-sensitive surface. Keeping
  it in its own assembly means a Fusion 17 shift costs one project, not the annotation model.
