using System.Runtime.InteropServices;
using System.Text;
using ManagedBass;

namespace RadioPlayer.Services;

/// <summary>
/// Reads BASS's native ICY/Shoutcast tag blocks. The one place that decides how those bytes become
/// strings — <see cref="RadioEngine"/> and <see cref="StreamHarvester"/> both come through here.
///
/// <para>They used to carry a copy of this each, and both copies had the same two bugs. The
/// duplication is why the reported symptom (a "?" in a DJ mix track) came from the harvester's copy
/// while the obvious place to look was the engine's.</para>
/// </summary>
internal static class IcyTags
{
    /// <summary>Reads a single null-terminated tag (e.g. <see cref="TagType.META"/>).</summary>
    internal static string? ReadString(int handle, TagType type)
    {
        var ptr = Bass.ChannelGetTags(handle, type);
        return ptr == IntPtr.Zero ? null : Decode(ReadNativeString(ptr));
    }

    /// <summary>
    /// Reads a block of null-terminated strings closed by a double null — how BASS returns
    /// <see cref="TagType.ICY"/>'s <c>icy-name:…</c>, <c>icy-br:…</c> and friends.
    /// </summary>
    internal static IReadOnlyList<string> ReadBlock(int handle, TagType type)
        => SplitBlock(Bass.ChannelGetTags(handle, type));

    /// <inheritdoc cref="ReadBlock"/>
    internal static IReadOnlyList<string> SplitBlock(IntPtr ptr)
    {
        var result = new List<string>();
        if (ptr == IntPtr.Zero) return result;

        while (true)
        {
            // Advance by the bytes actually READ, never by re-counting the decoded string. Both old
            // copies did the latter, and a single Latin-1 byte decodes to U+FFFD — three bytes in
            // UTF-8 — so the pointer overshot by two and every later tag in the block was misread.
            // This block is where icy-name comes from, and SongHistoryFilter compares titles against
            // icy-name to catch a station announcing itself.
            var bytes = ReadNativeString(ptr);
            if (bytes.Length == 0) break; // the second null of the terminating pair
            result.Add(Decode(bytes));
            ptr += bytes.Length + 1;
        }
        return result;
    }

    /// <summary>Pulls one <c>key:value</c> line out of a block and returns its value, or null.</summary>
    internal static string? ValueOf(IReadOnlyList<string> block, string key)
    {
        foreach (var line in block)
        {
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[key.Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    /// <summary>Bytes from <paramref name="ptr"/> up to (not including) the null terminator. The cap
    /// is there because this is untrusted native memory: a block missing its terminator must not run
    /// away.</summary>
    private static byte[] ReadNativeString(IntPtr ptr, int maxBytes = 8192)
    {
        var length = 0;
        while (length < maxBytes && Marshal.ReadByte(ptr, length) != 0)
            length++;

        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return bytes;
    }

    /// <summary>
    /// Decodes one tag, working out the encoding rather than assuming it.
    ///
    /// <para>ICY metadata carries no encoding declaration. Shoutcast-era servers send
    /// <b>ISO-8859-1</b>; Icecast 2 and most modern stations send <b>UTF-8</b>. Assuming UTF-8
    /// unconditionally — via <c>Marshal.PtrToStringUTF8</c>, which cannot fail and silently
    /// substitutes U+FFFD — turned 011.fm's "Queensrÿche" into "Queensr?che" while UTF-8 stations'
    /// "Tiësto" and "KOMM NÄHER" came through fine. The corruption was per-station, not random.</para>
    ///
    /// <para>So: try UTF-8 <i>strictly</i>, then fall back to Latin-1, which cannot fail. Every
    /// ASCII tag decodes identically either way. The order matters — Latin-1 accepts UTF-8 bytes
    /// happily and would render "Tiësto" as mojibake without ever failing over.</para>
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
