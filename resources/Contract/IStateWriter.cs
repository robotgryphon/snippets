namespace Resources.Contract;

public interface IStateWriter<in T> where T : notnull
{
    ValueTask<State> UpdateAsync(T resource, State state, CancellationToken cancel);
    
    ValueTask<State> RevokeAsync(T resource, CancellationToken cancel);
}