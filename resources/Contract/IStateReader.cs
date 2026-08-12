namespace Resources.Contract;

public interface IStateReader<in T> where T : notnull
{
    ValueTask<State> GetStateAsync(T resource, CancellationToken cancel);
}