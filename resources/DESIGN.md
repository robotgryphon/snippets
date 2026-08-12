# Complex Resources — one service that fans out to many

## Problem

A family of single-resource service contracts, each keyed by one resource type:

```csharp
interface IStateReader<in T> where T : notnull {
    ValueTask<State> GetStateAsync(T resource, CancellationToken cancel);
}

interface IStateWriter<in T> where T : notnull {
    ValueTask<State> UpdateAsync(T resource, State state, CancellationToken cancel);
    ValueTask        RevokeAsync(T resource, CancellationToken cancel);
}
```

Concrete services are registered per resource (`IStateReader<LocalUser>`, `IStateReader<RemoteUser>`, …).
We need a **complex resource** — a `User` that is a `LocalUser` *and* a `RemoteUser` treated as one —
that satisfies the *same* contracts (`IStateReader<User>`, `IStateWriter<User>`, …) by forwarding each
call to the per-sub-resource services, collecting the results, and merging.

The services vary: multiple methods, and **pass-through parameters** that aren't the resource
(`UpdateAsync`'s `state`). Any hand-rolled core that assumes `(resource, cancellation) → ValueTask<T>`
can't express that shape — so the implementation is generated from the contract interface itself.

## Approach

Everything is declared on the resource: its decomposition (`[SubResource]`) and which contracts to
generate services for (`[GenerateComplexService]`, once per contract). There is **no hand-written
service class and no hand-written merge** — the merge lives on the result type.

```csharp
[ComplexResource]
[GenerateComplexService(typeof(IStateReader<>))]
[GenerateComplexService(typeof(IStateWriter<>))]
public readonly partial record struct User(
    [property: SubResource] LocalUser Local,
    [property: SubResource] RemoteUser Remote);

// The merge lives once on the result type, not once per method:
public sealed record State(IReadOnlyCollection<string> Flags) : IMergeable<State>
{
    public static State Merge(IReadOnlyList<State> parts) => new(parts.SelectMany(p => p.Flags).Distinct().ToArray());
}
```

For each method on each contract the generator:

1. finds the parameter whose type is the interface's type parameter `T` — the *resource* param;
2. projects it per sub-resource via the `[SubResource]` decomposition (`resource.Local`, `resource.Remote`);
3. **forwards every other argument unchanged** (`state`, `cancel`, …) to each sub-service;
4. fans out concurrently (`Task.WhenAll`), then, for result-returning methods, folds inline via the
   result type's `IMergeable<T>.Merge`. Void methods (`ValueTask`/`Task`) just await — no merge.

Generated `ComplexStateWriter` (abridged) — a complete, `sealed` class you never write:

```csharp
public sealed partial class ComplexStateWriter : global::…IStateWriter<global::…User>
{
    private readonly global::…IStateWriter<global::…LocalUser> _local;
    private readonly global::…IStateWriter<global::…RemoteUser> _remote;
    public ComplexStateWriter(/* both sub-writers */) { … }

    public async ValueTask<State> UpdateAsync(User resource, State state, CancellationToken cancel)
    {
        var __results = await Task.WhenAll(
            _local.UpdateAsync(resource.Local, state, cancel).AsTask(),
            _remote.UpdateAsync(resource.Remote, state, cancel).AsTask()).ConfigureAwait(false);
        return global::…State.Merge(__results);   // inlined; no per-method hook
    }

    public async ValueTask RevokeAsync(User resource, CancellationToken cancel)
    {
        await Task.WhenAll(
            _local.RevokeAsync(resource.Local, cancel).AsTask(),
            _remote.RevokeAsync(resource.Remote, cancel).AsTask()).ConfigureAwait(false);
    }
}
```

### Why generation, and why merge-on-the-result-type

- The decomposition is a list of **heterogeneously-typed projections** (`User→LocalUser`,
  `User→RemoteUser`) — that can't be a generic type, and reflection would cost type-safety and
  AOT/trim-safety. A source generator derives it from the type at compile time.
- Forwarding **any** contract method means reading the interface's method signatures — again a
  compile-time, interface-driven job.
- The merge is not per-*method* policy, it's per-*result-type*: every `State` folds the same way.
  Putting it on the result type via `IMergeable<T>` means it's written **once per type**, the
  generator calls it inline, and the service classes have no bodies left — so the attributes collapse
  onto the resource and the hand-written classes disappear entirely. A result type that isn't
  `IMergeable` is a `CR0004` error.
- Generated classes are `sealed partial`, so you can still add members in your own partial if needed.
  The default name is `Complex{contract without leading I}`; override with `[GenerateComplexService(…, Name = "…")]`.

## Diagnostics

The generator emits actionable errors instead of letting bad code fall through to obscure CS errors:

| Id | When |
| --- | --- |
| `CR0001` | the contract isn't a generic interface with exactly one type parameter |
| `CR0002` | the resource declares no `[SubResource]` members |
| `CR0003` | a contract method can't be forwarded (no resource-typed parameter, `ref`/`out`, or a non-`Task`/`ValueTask` return) |
| `CR0004` | a result type doesn't implement `IMergeable<T>` |

`CR0001` blocks a service; `CR0002` blocks the resource; `CR0003`/`CR0004` are per-method — the other
methods still generate. A resource can request several contracts and each generates independently.

## Layout

- `ComplexResources/` — the attributes and `IMergeable<T>` (abstractions consumers reference).
- `gen/` — the `netstandard2.0` incremental generator. Matches attributes by **string metadata name**
  (a generator runs in the compiler's analyzer load context and can't load the net10 attributes
  assembly), and carries a fully value-equatable model (`EquatableArray<T>`, serialized `LocationInfo`)
  so incremental caching holds.
- `resources/` — the sample: contracts, `State` (an `IMergeable`), and `User` carrying the attributes.
- `tests/` — drives the generator with `CSharpGeneratorDriver` (source-shape + compile-clean +
  diagnostics) and one behavioral test that emits the generated assembly, loads it, and runs the
  fan-out for real.

## Notes for maintainers

- Supported return shapes: `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`. Sync and `ref`/`out` are
  rejected (`CR0003`); extend `Classify` / the parameter check to widen support.
- Extra injected dependencies: declare your own constructor on a partial of the generated class and
  chain to the generated one. When the generator sees an author-declared constructor it emits its
  sub-service constructor as `private` (otherwise `public`), so:

  ```csharp
  public sealed partial class ComplexStateReader
  {
      private readonly IClock _clock;
      public ComplexStateReader(IStateReader<LocalUser> local, IStateReader<RemoteUser> remote, IClock clock)
          : this(local, remote)   // generated private ctor wires the sub-services
      { _clock = clock; }
  }
  ```

- A custom per-method merge isn't supported — merge is uniform per result type by design; if you need
  method-specific folding, that's a future hook (e.g. an optional `partial` override).
- Source-generator output is cached by the Roslyn build server; if regenerated output looks stale
  after a generator change, `dotnet build-server shutdown` and rebuild.
