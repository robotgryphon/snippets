# External GraphQL demo

A gateway composing one source schema that is an external service rather than a project.

## Adding this to an existing AppHost

**1. Reference the integration.** In your AppHost `.csproj`:

```xml
<ProjectReference Include="path/to/src/ExternalGraphQL/ExternalGraphQL.csproj"
                  IsAspireProjectResource="false" />
```

`IsAspireProjectResource="false"` is required, not optional. The AppHost SDK defaults every
`ProjectReference` to `true`, which generates a `Projects.*` class for it and sets
`ReferenceOutputAssembly=false`, `ExcludeAssets=all`, and `Private=false` — the assembly is treated as
a resource to launch and is never referenced as code, so `using ExternalGraphQL;` will not resolve.
A library has to opt out.

Your AppHost also needs `HotChocolate.Fusion.Aspire` — `ExternalGraphQL` reaches into its internals
and does not re-export it.

**2. Three calls in `AppHost.cs`:**

```csharp
using ExternalGraphQL;

builder.AddGraphQLOrchestrator();

var catalog = builder.AddExternalService("catalog", "https://catalog.example.com");

var catalogSchema = builder
    .AddExternalGraphQL("catalog-graphql", catalog)
    .WithDownloadedGraphQLSchema(
        schemaDownloadPath: "/graphql?sdl",
        graphQLPath: "/graphql",
        sourceSchemaName: "catalog");

builder.AddProject<Projects.Gateway>("gateway")
    .WithReference(catalogSchema)
    .WithNitroComposition();
```

`WithReference` is not decoration. Composition walks the gateway's `ResourceRelationshipAnnotation`
and `EndpointReferenceAnnotation` entries and keeps the ones carrying a source schema annotation — a
source schema the gateway does not reference is silently not composed into it.

Pass the *resource builder returned by `AddExternalGraphQL`*, not the `AddExternalService` one. The
external service itself carries no source schema annotation and is not an `IResourceWithEndpoints`,
so referencing it composes nothing.

## What happens on `dotnet run`

1. `BeforeStartEvent` fires. Our subscriber resolves the external URL, allocates the endpoint, and
   writes `external/catalog-graphql/schema.graphqls` plus `schema-settings.json`.
2. Fusion's own `BeforeStartEvent` subscriber composes. It finds `catalog-graphql` through the
   gateway's reference, reads the schema off disk because the location is `ProjectDirectory`, and
   writes `gateway.far`.
3. The gateway starts and loads that archive.

Step 1 must precede step 2, and does so because our subscription is registered while the model is
built while Fusion's is registered at startup. See *Risks* in `DESIGN.md`.

## Layout

```
ExternalGraphQLDemo.AppHost/     wiring — the only file worth reading
ExternalGraphQLDemo.Gateway/     a stock Fusion gateway, nothing custom
external/                        generated at startup, gitignored
```

`external/` is written fresh on every start. Check it in only if you want the schema pinned, in which
case drop `WithDownloadedGraphQLSchema` for `WithGraphQLSourceSchema(SourceSchemaLocation.ProjectDirectory, …)`
and manage the files yourself.

## Before it runs

The AppHost points at `https://catalog.example.com`, which does not exist. Swap it for a real GraphQL
service and set `schemaDownloadPath` to whatever serves SDL there — `?sdl` is a HotChocolate
convention, not a universal one. The download is a plain HTTP GET expecting schema text back, which
is what Fusion's own fetcher does.

A URL that only resolves at runtime works too:

```csharp
var url = builder.AddParameter("catalog-url");
var catalog = builder.AddExternalService("catalog", url);
```

The parameter is resolved inside the startup hook, so this needs no other changes.
