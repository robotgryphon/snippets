using ComplexResources;

namespace Resources.Contract;

public sealed record State(IReadOnlyCollection<string> Flags) : IMergeable<State>
{
    // The merge lives here, once — every complex service that returns State folds with it.
    public static State Merge(IReadOnlyList<State> parts)
        => new(parts.SelectMany(p => p.Flags).Distinct().ToArray());
}
