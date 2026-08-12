namespace Resources.Contract;

// A response type we don't control — no merge logic lives here. The merge is supplied separately as
// an IMergeHandler<State> (see StateMergeHandler) and injected into the generated services.
public sealed record State(IReadOnlyCollection<string> Flags);
