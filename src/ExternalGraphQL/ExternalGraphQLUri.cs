namespace ExternalGraphQL;

/// <summary>
/// Path composition for external services hosted under a base path, e.g.
/// <c>https://api.dev/burgers/</c>.
/// </summary>
/// <remarks>
/// Deliberately not <c>new Uri(baseUri, relative)</c>: RFC 3986 resolves a relative reference
/// beginning with <c>/</c> against the authority, so <c>new Uri("https://api.dev/burgers/",
/// "/graphql")</c> is <c>https://api.dev/graphql</c> — the base path is silently dropped. Every path
/// here is meant as a suffix of the external service's URL, so composition is explicit.
/// </remarks>
internal static class ExternalGraphQLUri
{
    /// <summary>The base path of <paramref name="uri"/> without its trailing slash; empty at root.</summary>
    public static string BasePathOf(Uri? uri)
    {
        var path = uri?.AbsolutePath.TrimEnd('/');
        return string.IsNullOrEmpty(path) ? string.Empty : path;
    }

    /// <summary>
    /// Appends <paramref name="path"/> to <paramref name="basePath"/>. Any query string on
    /// <paramref name="path"/> is carried along untouched.
    /// </summary>
    public static string? CombinePath(string? basePath, string? path)
    {
        if (path is null || string.IsNullOrEmpty(basePath))
        {
            return path;
        }

        return path.StartsWith('/') ? basePath + path : $"{basePath}/{path}";
    }

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="baseUri"/>, preserving the base
    /// path.
    /// </summary>
    public static Uri Resolve(Uri baseUri, string path)
    {
        var combined = CombinePath(BasePathOf(baseUri), path) ?? "/";
        var queryIndex = combined.IndexOf('?');

        // UriBuilder keeps scheme, host, and port and lets path and query be replaced wholesale.
        // Reading back .Uri (not .ToString()) drops a default port again, so the request carries a
        // clean Host header.
        var builder = new UriBuilder(baseUri)
        {
            Path = queryIndex < 0 ? combined : combined[..queryIndex],
            Query = queryIndex < 0 ? string.Empty : combined[(queryIndex + 1)..]
        };

        return builder.Uri;
    }
}
