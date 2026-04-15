using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

/// <summary>
/// In-memory stub of IMonoEngine for unit testing without a real Mono process.
/// Returns configurable data for each method.
/// </summary>
public sealed class StubMonoEngine : IMonoEngine
{
    public bool IsInjected { get; set; }
    public string? MonoVersion { get; set; } = "6.12.0.182";
    public List<MonoDomain> Domains { get; set; } = [];
    public List<MonoAssembly> Assemblies { get; set; } = [];
    public Dictionary<(nuint Image, string Ns, string Name), MonoClass> Classes { get; set; } = new();
    public List<MonoField> Fields { get; set; } = [];
    public List<MonoMethod> Methods { get; set; } = [];
    public MonoInvokeResult? NextInvokeResult { get; set; }
    public byte[]? NextFieldValue { get; set; }
    public bool NextSetFieldResult { get; set; } = true;

    public Task<MonoInjectResult> InjectAsync(int processId, CancellationToken ct = default)
    {
        IsInjected = true;
        return Task.FromResult(new MonoInjectResult(true, MonoVersion));
    }

    public Task<bool> EjectAsync(int processId, CancellationToken ct = default)
    {
        IsInjected = false;
        return Task.FromResult(true);
    }

    public MonoStatus GetStatus(int processId) =>
        new(IsInjected, MonoVersion, Domains.Count, IsInjected ? MonoAgentHealth.Healthy : MonoAgentHealth.Unknown);

    public Task<IReadOnlyList<MonoDomain>> EnumDomainsAsync(int processId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MonoDomain>>(Domains);

    public Task<IReadOnlyList<MonoAssembly>> EnumAssembliesAsync(int processId, nuint domainHandle, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MonoAssembly>>(Assemblies);

    public Task<MonoClass?> FindClassAsync(int processId, nuint imageHandle, string namespaceName, string className, CancellationToken ct = default) =>
        Task.FromResult(Classes.TryGetValue((imageHandle, namespaceName, className), out var cls) ? cls : null);

    public Task<IReadOnlyList<MonoField>> EnumFieldsAsync(int processId, nuint classHandle, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MonoField>>(Fields);

    public Task<IReadOnlyList<MonoMethod>> EnumMethodsAsync(int processId, nuint classHandle, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MonoMethod>>(Methods);

    public Task<byte[]?> GetStaticFieldValueAsync(int processId, nuint classHandle, nuint fieldHandle, int size, CancellationToken ct = default) =>
        Task.FromResult(NextFieldValue);

    public Task<bool> SetStaticFieldValueAsync(int processId, nuint classHandle, nuint fieldHandle, byte[] value, CancellationToken ct = default) =>
        Task.FromResult(NextSetFieldResult);

    public Task<MonoInvokeResult> InvokeMethodAsync(int processId, nuint methodHandle, nuint instanceHandle, nuint[]? args = null, CancellationToken ct = default) =>
        Task.FromResult(NextInvokeResult ?? new MonoInvokeResult(true, 0));
}
