using Xunit;

namespace ComplexResources.Tests;

public class GeneratorTests
{
    // Shared contracts + resource. State is a plain type we don't control — its merge is supplied as
    // an injected IMergeHandler<State>, never on the type. User carries [ComplexResource]/[SubResource]
    // but no [GenerateComplexService]; each test adds those on its own partial declaration.
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using ComplexResources;

        namespace Sample
        {
            public sealed record State(IReadOnlyCollection<string> Flags);

            public readonly record struct LocalUser(string Id);
            public readonly record struct RemoteUser(string Id);

            [ComplexResource]
            public readonly partial record struct User(
                [property: SubResource] LocalUser Local,
                [property: SubResource] RemoteUser Remote);

            public interface IStateReader<in T> where T : notnull
            {
                ValueTask<State> GetStateAsync(T resource, CancellationToken cancel);
            }

            public interface IStateWriter<in T> where T : notnull
            {
                ValueTask<State> UpdateAsync(T resource, State state, CancellationToken cancel);
                ValueTask RevokeAsync(T resource, CancellationToken cancel); // void shape → no merge
            }
        }
        """;

    // Fakes + a State merge handler, for the behavioral (emit-and-run) tests.
    private const string ReaderFakes = """
        namespace Sample
        {
            public sealed class FakeLocal : IStateReader<LocalUser>
            {
                public ValueTask<State> GetStateAsync(LocalUser r, CancellationToken c) => new(new State(new[] { "local" }));
            }

            public sealed class FakeRemote : IStateReader<RemoteUser>
            {
                public ValueTask<State> GetStateAsync(RemoteUser r, CancellationToken c) => new(new State(new[] { "remote" }));
            }

            public sealed class StateMerge : IMergeHandler<State>
            {
                public State Merge(IReadOnlyList<State> parts) => new(parts.SelectMany(p => p.Flags).Distinct().ToArray());
            }
        }
        """;

    private static string Source(string body) => Preamble + "\n" + body;

    private const string ReaderOnUser = """
        namespace Sample
        {
            [GenerateComplexService(typeof(IStateReader<>))]
            public readonly partial record struct User;
        }
        """;

    [Fact]
    public void Generates_from_the_resource_and_injects_a_merge_handler()
    {
        var run = GeneratorHarness.Run(Source(ReaderOnUser));

        Assert.Empty(run.CompileErrors);
        var generated = run.SingleGenerated();
        Assert.Contains("public sealed partial class ComplexStateReader : global::Sample.IStateReader<global::Sample.User>", generated);
        // The merge handler is a constructor dependency, not a method or a call on the result type.
        Assert.Contains("global::ComplexResources.IMergeHandler<global::Sample.State> mergeState", generated);
        Assert.Contains("_local.GetStateAsync(resource.Local, cancel).AsTask()", generated);
        Assert.Contains("return _mergeState.Merge(__results);", generated);
    }

    [Fact]
    public void Writer_forwards_passthrough_args_and_merges_only_result_methods()
    {
        var body = """
            namespace Sample
            {
                [GenerateComplexService(typeof(IStateWriter<>))]
                public readonly partial record struct User;
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.Empty(run.CompileErrors);
        var generated = run.SingleGenerated();
        Assert.Contains("_local.UpdateAsync(resource.Local, state, cancel).AsTask()", generated);
        Assert.Contains("return _mergeState.Merge(__results);", generated);
        Assert.Contains("_local.RevokeAsync(resource.Local, cancel).AsTask()", generated);
        // One handler injected (State used once), and only UpdateAsync merges.
        Assert.Equal(1, CountOccurrences(generated, "IMergeHandler<global::Sample.State> mergeState"));
        Assert.Equal(1, CountOccurrences(generated, ".Merge(__results)"));
    }

    [Fact]
    public void Multiple_contracts_generate_multiple_services()
    {
        var body = """
            namespace Sample
            {
                [GenerateComplexService(typeof(IStateReader<>))]
                [GenerateComplexService(typeof(IStateWriter<>))]
                public readonly partial record struct User;
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.Empty(run.CompileErrors);
        Assert.Equal(2, run.GeneratedSources.Count);
        Assert.Contains(run.GeneratedSources, s => s.Contains("class ComplexStateReader"));
        Assert.Contains(run.GeneratedSources, s => s.Contains("class ComplexStateWriter"));
    }

    [Fact]
    public void Fan_out_and_injected_merge_actually_run()
    {
        var probe = """
            namespace Sample
            {
                public static class Probe
                {
                    public static string[] Run()
                    {
                        var service = new ComplexStateReader(new FakeLocal(), new FakeRemote(), new StateMerge());
                        return service
                            .GetStateAsync(new User(new LocalUser("l"), new RemoteUser("r")), default)
                            .GetAwaiter().GetResult().Flags.ToArray();
                    }
                }
            }
            """;

        var run = GeneratorHarness.Run(Source(ReaderOnUser + "\n" + ReaderFakes + "\n" + probe));
        var assembly = GeneratorHarness.EmitAndLoad(run);

        var result = (string[])assembly.GetType("Sample.Probe")!.GetMethod("Run")!.Invoke(null, null)!;

        Assert.Equal(new[] { "local", "remote" }, result);
    }

    [Fact]
    public void Author_constructor_makes_generated_constructor_private_and_chainable()
    {
        // The author declares an extra dependency and chains to the generated constructor, passing the
        // sub-services and the merge handler through.
        var body = """
            namespace Sample
            {
                public interface IClock { }
                public sealed class SystemClock : IClock { }

                [GenerateComplexService(typeof(IStateReader<>))]
                public readonly partial record struct User;

                public sealed partial class ComplexStateReader
                {
                    private readonly IClock _clock;
                    public ComplexStateReader(
                        IStateReader<LocalUser> local, IStateReader<RemoteUser> remote,
                        IMergeHandler<State> mergeState, IClock clock)
                        : this(local, remote, mergeState)
                    {
                        _clock = clock;
                    }
                }

                public static class Probe
                {
                    public static string[] Run()
                    {
                        var service = new ComplexStateReader(new FakeLocal(), new FakeRemote(), new StateMerge(), new SystemClock());
                        return service
                            .GetStateAsync(new User(new LocalUser("l"), new RemoteUser("r")), default)
                            .GetAwaiter().GetResult().Flags.ToArray();
                    }
                }
            }
            """;

        var run = GeneratorHarness.Run(Source(body + "\n" + ReaderFakes));

        Assert.Empty(run.CompileErrors);
        Assert.Contains("private ComplexStateReader(", run.SingleGenerated());

        var assembly = GeneratorHarness.EmitAndLoad(run);
        var result = (string[])assembly.GetType("Sample.Probe")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal(new[] { "local", "remote" }, result);
    }

    [Fact]
    public void Custom_name_is_honored()
    {
        var body = """
            namespace Sample
            {
                [GenerateComplexService(typeof(IStateReader<>), Name = "FannedStateReader")]
                public readonly partial record struct User;
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.Empty(run.CompileErrors);
        Assert.Contains("class FannedStateReader", run.SingleGenerated());
    }

    [Fact]
    public void CR0001_when_contract_is_not_a_single_parameter_interface()
    {
        var body = """
            namespace Sample
            {
                [GenerateComplexService(typeof(System.IDisposable))]
                public readonly partial record struct User;
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.True(run.HasError("CR0001"));
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void CR0002_when_resource_has_no_subresources()
    {
        var body = """
            namespace Sample
            {
                [ComplexResource]
                [GenerateComplexService(typeof(IStateReader<>))]
                public readonly partial record struct Bare(int X);
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.True(run.HasError("CR0002"));
        Assert.Empty(run.GeneratedSources);
    }

    [Fact]
    public void CR0003_when_a_method_cannot_be_forwarded()
    {
        var body = """
            namespace Sample
            {
                public interface ISyncContract<in T> where T : notnull
                {
                    int Compute(T resource); // synchronous return is unsupported
                }

                [GenerateComplexService(typeof(ISyncContract<>))]
                public readonly partial record struct User;
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.True(run.HasError("CR0003"));
        Assert.Empty(run.GeneratedSources);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
