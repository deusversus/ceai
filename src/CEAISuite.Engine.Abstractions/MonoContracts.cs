namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// Mono/.NET runtime introspection engine — enables inspection and manipulation
/// of managed code in Unity (Mono) and .NET processes. Mirrors CE's mono_* API.
///
/// Architecture: A native C agent DLL is injected into the target process. It
/// resolves Mono C API exports (mono_domain_get, mono_class_from_name, etc.)
/// and communicates with the host via named pipe IPC.
/// </summary>
public interface IMonoEngine
{
    /// <summary>Inject the Mono agent DLL into the target process. The agent locates mono.dll
    /// exports and starts the named pipe server.</summary>
    Task<MonoInjectResult> InjectAsync(int processId, CancellationToken ct = default);

    /// <summary>Eject the Mono agent from the target process.</summary>
    Task<bool> EjectAsync(int processId, CancellationToken ct = default);

    /// <summary>Check if the Mono agent is currently injected and responsive.</summary>
    MonoStatus GetStatus(int processId);

    // ── Domain & Assembly Enumeration ──

    /// <summary>Enumerate all Mono application domains in the target process.</summary>
    Task<IReadOnlyList<MonoDomain>> EnumDomainsAsync(int processId, CancellationToken ct = default);

    /// <summary>Enumerate assemblies loaded in a domain.</summary>
    Task<IReadOnlyList<MonoAssembly>> EnumAssembliesAsync(int processId, nuint domainHandle, CancellationToken ct = default);

    // ── Class & Type Introspection ──

    /// <summary>Find a class by namespace and name within an assembly image.</summary>
    Task<MonoClass?> FindClassAsync(int processId, nuint imageHandle, string namespaceName, string className, CancellationToken ct = default);

    /// <summary>Enumerate fields of a class.</summary>
    Task<IReadOnlyList<MonoField>> EnumFieldsAsync(int processId, nuint classHandle, CancellationToken ct = default);

    /// <summary>Enumerate methods of a class.</summary>
    Task<IReadOnlyList<MonoMethod>> EnumMethodsAsync(int processId, nuint classHandle, CancellationToken ct = default);

    // ── Field Access ──

    /// <summary>Read a static field value. Returns raw bytes.</summary>
    Task<byte[]?> GetStaticFieldValueAsync(int processId, nuint classHandle, nuint fieldHandle, int size, CancellationToken ct = default);

    /// <summary>Write a static field value from raw bytes.</summary>
    Task<bool> SetStaticFieldValueAsync(int processId, nuint classHandle, nuint fieldHandle, byte[] value, CancellationToken ct = default);

    // ── Method Invocation ──

    /// <summary>Invoke a method on a managed object (or static method with instance=0).
    /// Returns the raw pointer-sized return value.</summary>
    Task<MonoInvokeResult> InvokeMethodAsync(int processId, nuint methodHandle, nuint instanceHandle,
        nuint[]? args = null, CancellationToken ct = default);
}

// ── Records ──

/// <summary>Result of Mono agent injection.</summary>
public sealed record MonoInjectResult(bool Success, string? MonoVersion = null, string? Error = null);

/// <summary>Current status of the Mono agent for a process.</summary>
public sealed record MonoStatus(
    bool IsInjected,
    string? MonoVersion,
    int DomainCount,
    MonoAgentHealth Health = MonoAgentHealth.Unknown);

/// <summary>Health of the Mono agent.</summary>
public enum MonoAgentHealth
{
    Healthy,
    Unresponsive,
    Unknown
}

/// <summary>A Mono application domain.</summary>
public sealed record MonoDomain(nuint Handle, string Name, int AssemblyCount);

/// <summary>A loaded Mono assembly.</summary>
public sealed record MonoAssembly(nuint Handle, nuint ImageHandle, string Name, string FullName);

/// <summary>A Mono class (type).</summary>
public sealed record MonoClass(nuint Handle, string Namespace, string Name, nuint ParentHandle, int FieldCount, int MethodCount)
{
    /// <summary>Full name: "Namespace.ClassName"</summary>
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
}

/// <summary>A field on a Mono class.</summary>
public sealed record MonoField(nuint Handle, string Name, string TypeName, int Offset, bool IsStatic);

/// <summary>A method on a Mono class.</summary>
public sealed record MonoMethod(nuint Handle, string Name, string ReturnType, IReadOnlyList<string> ParameterTypes, bool IsStatic);

/// <summary>Result of invoking a managed method.</summary>
public sealed record MonoInvokeResult(bool Success, nuint ReturnValue = 0, string? Error = null);
