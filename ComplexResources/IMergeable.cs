namespace ComplexResources;

/// <summary>
/// A result type that knows how to fold several of itself into one. Generated complex services call
/// <see cref="Merge"/> inline for every result-returning contract method, so the merge lives once on
/// the type instead of once per method.
/// </summary>
public interface IMergeable<TSelf> where TSelf : IMergeable<TSelf>
{
    static abstract TSelf Merge(IReadOnlyList<TSelf> parts);
}
