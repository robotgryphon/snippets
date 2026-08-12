namespace ComplexResources;

/// <summary>
/// Folds several results of type <typeparamref name="T"/> into one. A generated complex service takes
/// one of these per distinct result type as a constructor dependency and calls it for every
/// result-returning contract method — so the merge lives outside both the result type and the
/// generator, and is supplied by DI.
/// </summary>
public interface IMergeHandler<T>
{
    T Merge(IReadOnlyList<T> parts);
}
