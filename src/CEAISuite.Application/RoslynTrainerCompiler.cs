using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CEAISuite.Application;

/// <summary>
/// Compiles generated C# trainer source code into a standalone .exe using Roslyn.
/// The trainer code uses System, System.Diagnostics, System.Runtime.InteropServices,
/// and System.Console — all resolved from the current runtime's trusted assemblies.
/// </summary>
public sealed class RoslynTrainerCompiler
{
    /// <summary>Result of a trainer compilation attempt.</summary>
    public sealed record TrainerCompilationResult(
        bool Success,
        string? OutputPath,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Compile trainer C# source to a standalone console .exe.
    /// </summary>
    /// <param name="source">The C# source code (from ScriptGenerationService.GenerateTrainerScript).</param>
    /// <param name="outputPath">Full path for the output .exe file.</param>
    /// <param name="assemblyName">Assembly name (default: "Trainer").</param>
    public static TrainerCompilationResult Compile(string source, string outputPath, string assemblyName = "Trainer")
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = GetRuntimeReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: OptimizationLevel.Release,
                platform: Platform.X64));

        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        var emitResult = compilation.Emit(outputStream);

        var errors = emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        var warnings = emitResult.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToList();

        if (!emitResult.Success)
        {
            // Clean up the failed output file
            outputStream.Close();
            try { File.Delete(outputPath); } catch { }
        }

        return new TrainerCompilationResult(
            emitResult.Success,
            emitResult.Success ? outputPath : null,
            errors,
            warnings);
    }

    /// <summary>
    /// Validate that trainer source compiles without errors (does not produce an .exe).
    /// </summary>
    public static TrainerCompilationResult Validate(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = GetRuntimeReferences();

        var compilation = CSharpCompilation.Create(
            "TrainerValidation",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        var diagnostics = compilation.GetDiagnostics();

        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        var warnings = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Warning)
            .Select(d => d.ToString())
            .ToList();

        return new TrainerCompilationResult(errors.Count == 0, null, errors, warnings);
    }

    /// <summary>
    /// Resolve metadata references from the current .NET runtime's trusted platform assemblies.
    /// The trainer source uses: System, System.Runtime, System.Diagnostics.Process,
    /// System.Runtime.InteropServices, System.Console, System.Linq.
    /// </summary>
    private static List<MetadataReference> GetRuntimeReferences()
    {
        var references = new List<MetadataReference>();

        // Use the Trusted Platform Assemblies list — the definitive set of runtime assemblies
        var tpaList = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (tpaList is not null)
        {
            // The trainer needs: System.Runtime, System.Console, System.Diagnostics.Process,
            // System.Runtime.InteropServices, System.Collections, System.Linq, System.Private.CoreLib
            var neededAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.Runtime",
                "System.Runtime.InteropServices",
                "System.Console",
                "System.Diagnostics.Process",
                "System.Collections",
                "System.Linq",
                "System.Private.CoreLib",
                "System.ComponentModel.Primitives",
                "System.Threading",
                "netstandard"
            };

            foreach (var assemblyPath in tpaList.Split(Path.PathSeparator))
            {
                var fileName = Path.GetFileNameWithoutExtension(assemblyPath);
                if (neededAssemblies.Contains(fileName))
                    references.Add(MetadataReference.CreateFromFile(assemblyPath));
            }
        }
        else
        {
            // Fallback: resolve from typeof() locations
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(System.Console).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(System.Diagnostics.Process).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.DllImportAttribute).Assembly.Location));

            // System.Runtime is needed for fundamental types
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var systemRuntime = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(systemRuntime))
                references.Add(MetadataReference.CreateFromFile(systemRuntime));
        }

        return references;
    }
}
