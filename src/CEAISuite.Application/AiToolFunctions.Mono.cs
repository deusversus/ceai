using CEAISuite.Application.AgentLoop;
using System.ComponentModel;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Inject the Mono introspection agent into the target process. Required before using any mono_* tools. The target must have mono.dll loaded (Unity/Mono games).")]
    public async Task<string> InjectMonoAgent(
        [Description("Process ID")] int processId)
    {
        if (monoEngine is null) return "Mono engine not available.";
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";

        try
        {
            var result = await monoEngine.InjectAsync(processId).ConfigureAwait(false);
            if (!result.Success) return $"Mono agent injection failed: {result.Error}";
            return ToJson(new { success = true, monoVersion = result.MonoVersion ?? "unknown" });
        }
        catch (Exception ex) { return $"InjectMonoAgent failed: {ex.Message}"; }
    }

    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Eject the Mono agent from the target process.")]
    public async Task<string> EjectMonoAgent(
        [Description("Process ID")] int processId)
    {
        if (monoEngine is null) return "Mono engine not available.";
        var result = await monoEngine.EjectAsync(processId).ConfigureAwait(false);
        return result ? "Mono agent ejected." : "Failed to eject Mono agent.";
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Get Mono agent status for a process.")]
    public Task<string> GetMonoStatus(
        [Description("Process ID")] int processId)
    {
        if (monoEngine is null) return Task.FromResult("Mono engine not available.");
        var status = monoEngine.GetStatus(processId);
        return Task.FromResult(ToJson(new
        {
            isInjected = status.IsInjected,
            monoVersion = status.MonoVersion,
            health = status.Health.ToString(),
            domainCount = status.DomainCount
        }));
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Enumerate Mono application domains in the target process. Requires Mono agent to be injected.")]
    public async Task<string> EnumMonoDomains(
        [Description("Process ID")] int processId)
    {
        if (monoEngine is null) return "Mono engine not available.";
        try
        {
            var domains = await monoEngine.EnumDomainsAsync(processId).ConfigureAwait(false);
            return ToJson(new
            {
                count = domains.Count,
                domains = domains.Select(d => new { handle = $"0x{(ulong)d.Handle:X}", d.Name, d.AssemblyCount })
            });
        }
        catch (Exception ex) { return $"EnumMonoDomains failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Enumerate assemblies in a Mono domain. Returns name, image handle for use with FindMonoClass.")]
    public async Task<string> EnumMonoAssemblies(
        [Description("Process ID")] int processId,
        [Description("Domain handle (hex)")] string domainHandle)
    {
        if (monoEngine is null) return "Mono engine not available.";
        try
        {
            var handle = ParseAddress(domainHandle);
            var assemblies = await monoEngine.EnumAssembliesAsync(processId, handle).ConfigureAwait(false);
            return ToJson(new
            {
                count = assemblies.Count,
                assemblies = assemblies.Select(a => new
                {
                    handle = $"0x{(ulong)a.Handle:X}",
                    imageHandle = $"0x{(ulong)a.ImageHandle:X}",
                    a.Name,
                    a.FullName
                })
            });
        }
        catch (Exception ex) { return $"EnumMonoAssemblies failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("Find a Mono class by namespace and name within an assembly image.")]
    public async Task<string> FindMonoClass(
        [Description("Process ID")] int processId,
        [Description("Image handle (hex, from EnumMonoAssemblies)")] string imageHandle,
        [Description("C# namespace (e.g. 'UnityEngine')")] string namespaceName,
        [Description("Class name (e.g. 'GameObject')")] string className)
    {
        if (monoEngine is null) return "Mono engine not available.";
        try
        {
            var handle = ParseAddress(imageHandle);
            var cls = await monoEngine.FindClassAsync(processId, handle, namespaceName, className).ConfigureAwait(false);
            if (cls is null) return $"Class '{namespaceName}.{className}' not found in image 0x{(ulong)handle:X}.";
            return ToJson(new
            {
                handle = $"0x{(ulong)cls.Handle:X}",
                cls.Namespace,
                cls.Name,
                cls.FullName,
                parentHandle = $"0x{(ulong)cls.ParentHandle:X}",
                cls.FieldCount,
                cls.MethodCount
            });
        }
        catch (Exception ex) { return $"FindMonoClass failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Enumerate fields of a Mono class. Shows name, type, offset, and whether static.")]
    public async Task<string> EnumMonoFields(
        [Description("Process ID")] int processId,
        [Description("Class handle (hex)")] string classHandle)
    {
        if (monoEngine is null) return "Mono engine not available.";
        try
        {
            var handle = ParseAddress(classHandle);
            var fields = await monoEngine.EnumFieldsAsync(processId, handle).ConfigureAwait(false);
            return ToJson(new
            {
                count = fields.Count,
                fields = fields.Select(f => new
                {
                    handle = $"0x{(ulong)f.Handle:X}",
                    f.Name,
                    f.TypeName,
                    f.Offset,
                    f.IsStatic
                })
            });
        }
        catch (Exception ex) { return $"EnumMonoFields failed: {ex.Message}"; }
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [Description("Enumerate methods of a Mono class. Shows name, return type, parameters.")]
    public async Task<string> EnumMonoMethods(
        [Description("Process ID")] int processId,
        [Description("Class handle (hex)")] string classHandle)
    {
        if (monoEngine is null) return "Mono engine not available.";
        try
        {
            var handle = ParseAddress(classHandle);
            var methods = await monoEngine.EnumMethodsAsync(processId, handle).ConfigureAwait(false);
            return ToJson(new
            {
                count = methods.Count,
                methods = methods.Select(m => new
                {
                    handle = $"0x{(ulong)m.Handle:X}",
                    m.Name,
                    m.ReturnType,
                    m.IsStatic,
                    parameters = m.ParameterTypes
                })
            });
        }
        catch (Exception ex) { return $"EnumMonoMethods failed: {ex.Message}"; }
    }

    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Invoke a Mono method. Use with caution — can crash the target process if arguments are wrong.")]
    public async Task<string> InvokeMonoMethod(
        [Description("Process ID")] int processId,
        [Description("Method handle (hex)")] string methodHandle,
        [Description("Instance handle (hex, or '0' for static)")] string instanceHandle = "0")
    {
        if (monoEngine is null) return "Mono engine not available.";
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        try
        {
            var method = ParseAddress(methodHandle);
            var instance = ParseAddress(instanceHandle);
            var result = await monoEngine.InvokeMethodAsync(processId, method, instance).ConfigureAwait(false);
            if (!result.Success) return $"Invoke failed: {result.Error}";
            return ToJson(new { success = true, returnValue = $"0x{(ulong)result.ReturnValue:X}" });
        }
        catch (Exception ex) { return $"InvokeMonoMethod failed: {ex.Message}"; }
    }
}
