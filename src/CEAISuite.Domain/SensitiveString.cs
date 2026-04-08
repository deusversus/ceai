using System.Runtime.InteropServices;
using System.Text;

namespace CEAISuite.Domain;

/// <summary>
/// A disposable wrapper for sensitive string data (API keys, tokens, etc.).
/// The backing <see cref="byte"/> array is pinned in memory via <see cref="GCHandle"/>
/// so the GC cannot relocate it, and it is zeroed on <see cref="Dispose"/>.
/// <para>
/// This is the modern replacement for the deprecated <c>SecureString</c>.
/// It does NOT encrypt the data in memory — the goal is to guarantee that
/// plaintext bytes are zeroed when no longer needed, preventing lingering
/// secrets in managed heap memory.
/// </para>
/// </summary>
public sealed class SensitiveString : IDisposable, IEquatable<SensitiveString>
{
    private readonly byte[] _bytes;
    private readonly GCHandle _pin;

    /// <summary>True after <see cref="Dispose"/> has been called and the backing memory zeroed.</summary>
    public bool IsDisposed { get; private set; }

    private SensitiveString(byte[] bytes)
    {
        _bytes = bytes;
        _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
    }

    /// <summary>
    /// Create a <see cref="SensitiveString"/> from plaintext.
    /// The source string cannot be zeroed (strings are immutable in .NET),
    /// but the backing byte array is pinned and will be zeroed on disposal.
    /// </summary>
    public static SensitiveString FromPlaintext(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        return new SensitiveString(bytes);
    }

    /// <summary>
    /// Returns the plaintext value. Throws <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    public override string ToString()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Encoding.UTF8.GetString(_bytes);
    }

    /// <summary>Implicit conversion for convenience at call-sites that expect <c>string</c>.</summary>
    public static implicit operator string(SensitiveString s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.ToString();
    }

    /// <inheritdoc/>
    public bool Equals(SensitiveString? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (IsDisposed || other.IsDisposed) return false;
        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SensitiveString);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (IsDisposed) return 0;
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }

    /// <summary>Zero the backing byte array and release the GC pin.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        // Zero every byte while still pinned
        Array.Clear(_bytes, 0, _bytes.Length);

        // Release the pin so the (now-zeroed) array can be collected
        if (_pin.IsAllocated)
            _pin.Free();
    }
}
