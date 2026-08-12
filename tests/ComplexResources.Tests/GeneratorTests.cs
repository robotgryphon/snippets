using Xunit;

namespace ComplexResources.Tests;

public class GeneratorTests
{
    // Shared contracts + resource. User carries [ComplexResource]/[SubResource] but no
    // [GenerateComplexService] — each test adds those on its own partial declaration, so diagnostic
    // tests stay clean. State provides the merge via IMergeable<State>.
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using ComplexResources;

        namespace Sample
        {
            public sealed record State(IReadOnlyCollection<string> Flags) : IMergeable<State>
            {
                public static State Merge(IReadOnlyList<State> parts)
                    => new(parts.SelectMany(p => p.Flags).Distinct().ToArray());
            }

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

    private static string Source(string body) => Preamble + "\n" + body;

    private const string ReaderOnUser = """
        namespace Sample
        {
            [GenerateComplexService(typeof(IStateReader<>))]
            public readonly partial record struct User;
        }
        """;

    [Fact]
    public void Generates_from_the_resource_with_inlined_merge()
    {
        var run = GeneratorHarness.Run(Source(ReaderOnUser));

        Assert.Empty(run.CompileErrors);
        var generated = run.SingleGenerated();
        Assert.Contains("public sealed partial class ComplexStateReader : global::Sample.IStateReader<global::Sample.User>", generated);
        Assert.Contains("public ComplexStateReader(", generated); // injectable by default
        Assert.Contains("_local.GetStateAsync(resource.Local, cancel).AsTask()", generated);
        // Merge is inlined as a call to the result type — no partial method.
        Assert.Contains("return global::Sample.State.Merge(__results);", generated);
        Assert.DoesNotContain("partial", generated.Replace("partial class", "")); // no leftover partial merge decls
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
        Assert.Contains("return global::Sample.State.Merge(__results);", generated);
        // The void RevokeAsync fans out but merges nothing.
        Assert.Contains("_local.RevokeAsync(resource.Local, cancel).AsTask()", generated);
        Assert.Equal(1, CountOccurrences(generated, ".Merge(__results)")); // only UpdateAsync merges
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
    public void Fan_out_and_merge_actually_run()
    {
        var probe = """
            namespace Sample
            {
                public sealed class FakeLocal : IStateReader<LocalUser>
                {
                    public ValueTask<State> GetStateAsync(LocalUser r, CancellationToken c)
                        => new(new State(new[] { "local" }));
                }

                public sealed class FakeRemote : IStateReader<RemoteUser>
                {
                    public ValueTask<State> GetStateAsync(RemoteUser r, CancellationToken c)
                        => new(new State(new[] { "remote" }));
                }

                public static class Probe
                {
                    public static string[] Run()
                    {
                        var service = new ComplexStateReader(new FakeLocal(), new FakeRemote());
                        var state = service
                            .GetStateAsync(new User(new LocalUser("l"), new RemoteUser("r")), default)
                            .GetAwaiter().GetResult();
                        return state.Flags.ToArray();
                    }
                }
            }
            """;

        var run = GeneratorHarness.Run(Source(ReaderOnUser + "\n" + probe));
        var assembly = GeneratorHarness.EmitAndLoad(run);

        var result = (string[])assembly.GetType("Sample.Probe")!
            .GetMethod("Run")!
            .Invoke(null, null)!;

        Assert.Equal(new[] { "local", "remote" }, result);
    }

    [Fact]
    public void Author_constructor_makes_generated_constructor_private_and_chainable()
    {
        // The author declares extra dependencies and chains to the generated constructor.
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
                        IStateReader<LocalUser> local, IStateReader<RemoteUser> remote, IClock clock)
                        : this(local, remote)
                    {
                        _clock = clock;
                    }
                }

                public sealed class FakeLocal : IStateReader<LocalUser>
                {
                    public ValueTask<State> GetStateAsync(LocalUser r, CancellationToken c) => new(new State(new[] { "local" }));
                }

                public sealed class FakeRemote : IStateReader<RemoteUser>
                {
                    public ValueTask<State> GetStateAsync(RemoteUser r, CancellationToken c) => new(new State(new[] { "remote" }));
                }

                public static class Probe
                {
                    public static string[] Run()
                    {
                        // Constructed via the author's constructor with the extra dependency.
                        var service = new ComplexStateReader(new FakeLocal(), new FakeRemote(), new SystemClock());
                        return service
                            .GetStateAsync(new User(new LocalUser("l"), new RemoteUser("r")), default)
                            .GetAwaiter().GetResult().Flags.ToArray();
                    }
                }
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.Empty(run.CompileErrors);
        Assert.Contains("private ComplexStateReader(", run.SingleGenerated());
        Assert.DoesNotContain("public ComplexStateReader(", run.SingleGenerated());

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

    [Fact]
    public void CR0004_when_result_type_is_not_mergeable()
    {
        var body = """
            namespace Sample
            {
                public sealed record Plain(int N); // does not implement IMergeable<Plain>

                public interface IPlainReader<in T> where T : notnull
                {
                    ValueTask<Plain> ReadAsync(T resource, CancellationToken cancel);
                }

                [GenerateComplexService(typeof(IPlainReader<>))]
                public readonly partial record struct User;
            }
            """;

        var run = GeneratorHarness.Run(Source(body));

        Assert.True(run.HasError("CR0004"));
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
