using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Generates unique byte signatures (AOB patterns) from code or data regions.
/// CE calls this "signature maker" — it reads bytes at an address and generates
/// a pattern with wildcards for relocatable bytes (like relative offsets).
/// </summary>
public sealed class SignatureGeneratorService(IEngineFacade engine)
{
    public sealed record SignatureResult(
        string Pattern,
        nuint Address,
        int Length,
        string Description);

    /// <summary>
    /// Generate an AOB signature at the given address.
    /// Wildcards are placed on likely relocation bytes (RIP-relative offsets in x64 code).
    /// </summary>
    public async Task<SignatureResult> GenerateAsync(
        int processId,
        nuint address,
        int length = 32,
        CancellationToken ct = default)
    {
        var result = await engine.ReadMemoryAsync(processId, address, length, ct);
        var bytes = result.Bytes.ToArray();

        // Simple approach: wildcard bytes that look like they could be relocatable offsets
        // In x64, instructions like CALL rel32, JMP rel32, MOV reg,[RIP+disp32] have 4-byte relative offsets
        var pattern = new string[bytes.Length];
        var wildcardPositions = FindRelocatableOffsets(bytes);

        for (var i = 0; i < bytes.Length; i++)
        {
            pattern[i] = wildcardPositions.Contains(i) ? "??" : bytes[i].ToString("X2");
        }

        var sig = string.Join(" ", pattern);
        return new SignatureResult(sig, address, length, $"Signature at 0x{address:X} ({length} bytes)");
    }

    /// <summary>
    /// Test if a signature uniquely matches in a module.
    /// Returns the number of matches found.
    /// </summary>
    public async Task<int> TestUniquenessAsync(
        int processId,
        string moduleName,
        string pattern,
        CancellationToken ct = default)
    {
        var attachment = await engine.AttachAsync(processId, ct);
        var module = attachment.Modules.FirstOrDefault(m =>
            string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));

        if (module is null) return -1;

        // Parse pattern
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var patternBytes = new byte[parts.Length];
        var wildcardMask = new bool[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "?" or "??" or "*")
                wildcardMask[i] = true;
            else
                patternBytes[i] = byte.Parse(parts[i], System.Globalization.NumberStyles.HexNumber);
        }

        var matchCount = 0;
        var chunkSize = 0x100000; // 1MB chunks

        for (long offset = 0; offset < module.SizeBytes; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var readSize = (int)Math.Min(chunkSize, module.SizeBytes - offset);
            if (readSize < parts.Length) break;

            try
            {
                var readAddr = (nuint)((long)module.BaseAddress + offset);
                var memResult = await engine.ReadMemoryAsync(processId, readAddr, readSize, ct);
                var bytes = memResult.Bytes.ToArray();

                for (var i = 0; i <= bytes.Length - parts.Length; i++)
                {
                    var match = true;
                    for (var j = 0; j < parts.Length; j++)
                    {
                        if (!wildcardMask[j] && bytes[i + j] != patternBytes[j])
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) matchCount++;
                }
            }
            catch { /* unreadable region */ }
        }

        return matchCount;
    }

    private static HashSet<int> FindRelocatableOffsets(byte[] bytes)
    {
        var wildcards = new HashSet<int>();

        // Look for common x64 instruction patterns with relative offsets
        for (var i = 0; i < bytes.Length; i++)
        {
            // CALL rel32 (E8 xx xx xx xx)
            if (bytes[i] == 0xE8 && i + 5 <= bytes.Length)
            {
                for (var j = 1; j <= 4 && i + j < bytes.Length; j++)
                    wildcards.Add(i + j);
            }
            // JMP rel32 (E9 xx xx xx xx)
            else if (bytes[i] == 0xE9 && i + 5 <= bytes.Length)
            {
                for (var j = 1; j <= 4 && i + j < bytes.Length; j++)
                    wildcards.Add(i + j);
            }
            // MOV reg, [RIP+disp32] and LEA patterns (48 8B/8D 05-3D xx xx xx xx)
            else if (i + 2 < bytes.Length && bytes[i] == 0x48 &&
                     (bytes[i + 1] == 0x8B || bytes[i + 1] == 0x8D) &&
                     (bytes[i + 2] & 0xC7) == 0x05 && i + 7 <= bytes.Length)
            {
                for (var j = 3; j <= 6 && i + j < bytes.Length; j++)
                    wildcards.Add(i + j);
            }
            // Conditional jumps rel32 (0F 80-8F xx xx xx xx)
            else if (bytes[i] == 0x0F && i + 1 < bytes.Length &&
                     bytes[i + 1] >= 0x80 && bytes[i + 1] <= 0x8F && i + 6 <= bytes.Length)
            {
                for (var j = 2; j <= 5 && i + j < bytes.Length; j++)
                    wildcards.Add(i + j);
            }
        }

        return wildcards;
    }
}
