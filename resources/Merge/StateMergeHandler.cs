using ComplexResources;
using Resources.Contract;

namespace Resources.Merge;

/// <summary>
/// The merge policy for <see cref="State"/>, implemented outside the type it merges. Registered in DI
/// as <c>IMergeHandler&lt;State&gt;</c>; the generated complex services take it as a constructor
/// dependency and call it for every State-returning method.
/// </summary>
public sealed class StateMergeHandler : IMergeHandler<State>
{
    public State Merge(IReadOnlyList<State> parts)
        => new(parts.SelectMany(p => p.Flags).Distinct().ToArray());
}
