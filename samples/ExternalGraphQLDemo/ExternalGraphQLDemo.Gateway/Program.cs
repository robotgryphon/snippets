var builder = WebApplication.CreateBuilder(args);

// gateway.far is the composed archive. The orchestrator writes it next to this project before the
// gateway starts, and rewrites it whenever a source schema restarts.
builder.AddGraphQLGateway()
    .AddFileSystemConfiguration("./gateway.far");

var app = builder.Build();

app.MapGraphQL();

app.Run();
