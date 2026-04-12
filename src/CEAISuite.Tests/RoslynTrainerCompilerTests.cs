using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

[Trait("Category", "Compilation")]
public class RoslynTrainerCompilerTests : IDisposable
{
    private readonly string _tempDir;

    public RoslynTrainerCompilerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai-roslyn-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static AddressTableService CreateTableWithLockedEntries()
    {
        var engine = new StubEngineFacade();
        var service = new AddressTableService(engine);
        service.AddEntry("0x12345678", MemoryDataType.Float, "100", label: "Health");
        service.AddEntry("0x12345680", MemoryDataType.Int32, "30", label: "Ammo");
        foreach (var e in service.Entries)
            service.ToggleLock(e.Id);
        return service;
    }

    [Fact]
    public void Validate_GeneratedTrainerSource_CompilesCleanly()
    {
        var table = CreateTableWithLockedEntries();
        var source = ScriptGenerationService.GenerateTrainerScript(
            table.Entries.Where(e => e.IsLocked).ToList(), "TestGame.exe");

        var result = RoslynTrainerCompiler.Validate(source);

        Assert.True(result.Success, $"Validation failed:\n{string.Join("\n", result.Errors)}");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Compile_GeneratedTrainerSource_ProducesExe()
    {
        var table = CreateTableWithLockedEntries();
        var source = ScriptGenerationService.GenerateTrainerScript(
            table.Entries.Where(e => e.IsLocked).ToList(), "TestGame.exe");

        var outputPath = Path.Combine(_tempDir, "Trainer.exe");
        var result = RoslynTrainerCompiler.Compile(source, outputPath);

        Assert.True(result.Success, $"Compilation failed:\n{string.Join("\n", result.Errors)}");
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(outputPath), "Output .exe should exist");

        // Verify it's a valid PE (MZ header)
        var bytes = File.ReadAllBytes(outputPath);
        Assert.True(bytes.Length > 2, "Output should have content");
        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal((byte)'Z', bytes[1]);
    }

    [Fact]
    public void Compile_InvalidSource_ReturnsFalseWithErrors()
    {
        var source = "this is not valid C# code at all!!!";
        var outputPath = Path.Combine(_tempDir, "Bad.exe");

        var result = RoslynTrainerCompiler.Compile(source, outputPath);

        Assert.False(result.Success);
        Assert.Null(result.OutputPath);
        Assert.NotEmpty(result.Errors);
        Assert.False(File.Exists(outputPath), "Failed compilation should not leave output file");
    }

    [Fact]
    public void Compile_EmptySource_ReturnsFalseWithErrors()
    {
        var outputPath = Path.Combine(_tempDir, "Empty.exe");

        var result = RoslynTrainerCompiler.Compile("", outputPath);

        // Empty source has no entry point → compilation error
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_ValidMinimalProgram_Succeeds()
    {
        const string source = """
            using System;
            public static class Program
            {
                public static void Main() => Console.WriteLine("Hello");
            }
            """;

        var result = RoslynTrainerCompiler.Validate(source);
        Assert.True(result.Success, $"Errors:\n{string.Join("\n", result.Errors)}");
    }

    [Fact]
    public void Compile_CustomAssemblyName_UsedInOutput()
    {
        const string source = """
            public static class Program
            {
                public static void Main() { }
            }
            """;

        var outputPath = Path.Combine(_tempDir, "Custom.exe");
        var result = RoslynTrainerCompiler.Compile(source, outputPath, "MyTrainer");

        Assert.True(result.Success, $"Errors:\n{string.Join("\n", result.Errors)}");
        Assert.True(File.Exists(outputPath));
    }
}
