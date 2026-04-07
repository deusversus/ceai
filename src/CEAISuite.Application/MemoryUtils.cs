namespace CEAISuite.Application;

/// <summary>Shared memory-related formatting utilities used across application services.</summary>
internal static class MemoryUtils
{
    /// <summary>Format a byte count as a human-readable size string (e.g., "1.2 MB").</summary>
    public static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} bytes"
        };
}
