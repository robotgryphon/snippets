using ExternalGraphQL;

var builder = DistributedApplication.CreateBuilder(args);

// Installs Fusion's startup hook: schema discovery and composition. Without this nothing scans for
// source schemas, and the gateway starts with whatever archive it already had.
builder.AddGraphQLOrchestrator();

// The GraphQL API we do not own. Swap this for your own; a parameter works too:
//     var url = builder.AddParameter("catalog-url");
//     var catalog = builder.AddExternalService("catalog", url);
var catalog = builder.AddExternalService("catalog", "https://catalog.example.com");

// Projects the external service into something Fusion can compose. On startup this downloads the
// SDL to external/catalog-graphql/schema.graphqls and registers it as a ProjectDirectory source
// schema.
var catalogSchema = builder
    .AddExternalGraphQL("catalog-graphql", catalog)
    .WithDownloadedGraphQLSchema(
        // A plain GET that returns schema text. "?sdl" is the HotChocolate convention; point this at
        // whatever your service serves SDL from.
        schemaDownloadPath: "/graphql?sdl",
        graphQLPath: "/graphql",
        sourceSchemaName: "catalog");

builder.AddProject<Projects.ExternalGraphQLDemo_Gateway>("gateway")
    // WithReference is what ties the source schema to this gateway: composition collects the
    // gateway's referenced resources and keeps the ones carrying a source schema annotation.
    .WithReference(catalogSchema)
    .WithNitroComposition();

builder.Build().Run();
