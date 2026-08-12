// Polyfill so `record` / init-only setters compile against netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
