using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ExternalGraphQL;

/// <summary>
/// Points Fusion at the directory the schema was downloaded into.
/// </summary>
/// <remarks>
/// Fusion only ever uses <see cref="ProjectPath"/> as
/// <c>Path.GetDirectoryName(projectPath)</c>, so the named project file does not have to exist. It
/// is still given a real <c>.csproj</c> name so anything that logs the path reads sensibly.
/// </remarks>
internal sealed class ExternalProjectMetadata(string projectPath) : IProjectMetadata
{
    public string ProjectPath { get; } = projectPath;

    /// <summary>Nothing to build — the directory holds a downloaded schema, not a compilable project.</summary>
    public bool SuppressBuild => true;

    public bool IsFileBasedApp => false;

    public LaunchSettings? LaunchSettings => null;
}
