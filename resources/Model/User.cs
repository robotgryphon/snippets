using ComplexResources;
using Resources.Contract;

namespace Resources.Model;

/// <summary>
/// The complex resource: a User is a LocalUser and a RemoteUser treated as one. Each
/// [GenerateComplexService] generates a full ComplexStateReader / ComplexStateWriter that fans out to
/// the per-sub-resource services and merges results via <see cref="State"/>.Merge — no hand-written
/// class or merge needed.
/// </summary>
[ComplexResource]
[GenerateComplexService(typeof(IStateReader<>))]
[GenerateComplexService(typeof(IStateWriter<>))]
public readonly partial record struct User(
    [property: SubResource] LocalUser Local,
    [property: SubResource] RemoteUser Remote);
