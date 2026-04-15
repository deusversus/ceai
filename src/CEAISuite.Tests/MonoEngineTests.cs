using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Phase 12E Mono/.NET introspection bridge:
/// MonoContracts records, StubMonoEngine, Lua bindings, and AI tools.
/// </summary>
public class MonoEngineTests
{
    // ──────────────────────────────────────────────────────────
    // Records
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void MonoClass_FullName_CombinesNamespaceAndName()
    {
        var cls = new MonoClass(0x100, "UnityEngine", "GameObject", 0, 10, 20);
        Assert.Equal("UnityEngine.GameObject", cls.FullName);
    }

    [Fact]
    public void MonoClass_FullName_OmitsEmptyNamespace()
    {
        var cls = new MonoClass(0x100, "", "Program", 0, 0, 0);
        Assert.Equal("Program", cls.FullName);
    }

    [Fact]
    public void MonoInjectResult_SuccessCarriesVersion()
    {
        var result = new MonoInjectResult(true, "6.12.0.182");
        Assert.True(result.Success);
        Assert.Equal("6.12.0.182", result.MonoVersion);
        Assert.Null(result.Error);
    }

    [Fact]
    public void MonoInjectResult_FailureCarriesError()
    {
        var result = new MonoInjectResult(false, Error: "mono.dll not found");
        Assert.False(result.Success);
        Assert.Equal("mono.dll not found", result.Error);
    }

    [Fact]
    public void MonoStatus_DefaultHealth()
    {
        var status = new MonoStatus(false, null, 0);
        Assert.Equal(MonoAgentHealth.Unknown, status.Health);
    }

    [Fact]
    public void MonoField_Record_Constructs()
    {
        var field = new MonoField(0x200, "health", "System.Single", 0x10, false);
        Assert.Equal("health", field.Name);
        Assert.Equal("System.Single", field.TypeName);
        Assert.False(field.IsStatic);
    }

    [Fact]
    public void MonoMethod_Record_Constructs()
    {
        string[] paramTypes = ["System.Single", "System.Boolean"];
        var method = new MonoMethod(0x300, "TakeDamage", "System.Void", paramTypes, false);
        Assert.Equal("TakeDamage", method.Name);
        Assert.Equal(2, method.ParameterTypes.Count);
    }

    // ──────────────────────────────────────────────────────────
    // StubMonoEngine
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Stub_InjectAsync_SetsIsInjected()
    {
        var engine = new StubMonoEngine();
        Assert.False(engine.IsInjected);

        var result = await engine.InjectAsync(1000);
        Assert.True(result.Success);
        Assert.True(engine.IsInjected);
    }

    [Fact]
    public async Task Stub_EjectAsync_ClearsIsInjected()
    {
        var engine = new StubMonoEngine { IsInjected = true };
        var result = await engine.EjectAsync(1000);
        Assert.True(result);
        Assert.False(engine.IsInjected);
    }

    [Fact]
    public void Stub_GetStatus_ReflectsInjectionState()
    {
        var engine = new StubMonoEngine { IsInjected = true, MonoVersion = "6.12" };
        var status = engine.GetStatus(1000);
        Assert.True(status.IsInjected);
        Assert.Equal("6.12", status.MonoVersion);
        Assert.Equal(MonoAgentHealth.Healthy, status.Health);
    }

    [Fact]
    public async Task Stub_EnumDomains_ReturnsConfiguredData()
    {
        var engine = new StubMonoEngine();
        engine.Domains.Add(new MonoDomain(0x100, "root", 5));

        var domains = await engine.EnumDomainsAsync(1000);
        Assert.Single(domains);
        Assert.Equal("root", domains[0].Name);
    }

    [Fact]
    public async Task Stub_FindClass_ReturnsMatchOrNull()
    {
        var engine = new StubMonoEngine();
        var cls = new MonoClass(0x200, "UnityEngine", "Transform", 0, 5, 10);
        engine.Classes[(0x100, "UnityEngine", "Transform")] = cls;

        var found = await engine.FindClassAsync(1000, 0x100, "UnityEngine", "Transform");
        Assert.NotNull(found);
        Assert.Equal("UnityEngine.Transform", found.FullName);

        var notFound = await engine.FindClassAsync(1000, 0x100, "UnityEngine", "Rigidbody");
        Assert.Null(notFound);
    }

    [Fact]
    public async Task Stub_InvokeMethod_ReturnsConfiguredResult()
    {
        var engine = new StubMonoEngine();
        engine.NextInvokeResult = new MonoInvokeResult(true, 42);

        var result = await engine.InvokeMethodAsync(1000, 0x300, 0);
        Assert.True(result.Success);
        Assert.Equal((nuint)42, result.ReturnValue);
    }

    [Fact]
    public async Task Stub_StaticField_ReadWrite()
    {
        var engine = new StubMonoEngine();
        engine.NextFieldValue = BitConverter.GetBytes(100);

        var data = await engine.GetStaticFieldValueAsync(1000, 0x100, 0x200, 4);
        Assert.NotNull(data);
        Assert.Equal(100, BitConverter.ToInt32(data));

        var written = await engine.SetStaticFieldValueAsync(1000, 0x100, 0x200, BitConverter.GetBytes(200));
        Assert.True(written);
    }

    // ──────────────────────────────────────────────────────────
    // IMonoEngine interface completeness
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void IMonoEngine_AllMethodsImplemented()
    {
        // Verify StubMonoEngine implements all IMonoEngine methods
        var interfaceMethods = typeof(IMonoEngine).GetMethods()
            .Select(m => m.Name)
            .ToHashSet();

        var stubMethods = typeof(StubMonoEngine).GetMethods()
            .Where(m => m.DeclaringType == typeof(StubMonoEngine))
            .Select(m => m.Name)
            .ToHashSet();

        var missing = interfaceMethods.Where(m => !stubMethods.Contains(m)).ToList();
        Assert.Empty(missing);
    }
}
