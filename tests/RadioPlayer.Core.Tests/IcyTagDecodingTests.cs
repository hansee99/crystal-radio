using System.Runtime.InteropServices;
using System.Text;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// ICY/Shoutcast metadata carries no encoding declaration. Shoutcast-era servers send ISO-8859-1;
/// Icecast 2 and most modern stations send UTF-8. Tag reading used to assume UTF-8 unconditionally
/// via <c>Marshal.PtrToStringUTF8</c>, which cannot fail and silently substitutes U+FFFD — so
/// 011.fm's "Queensrÿche" was stored as "Queensr?che" while UTF-8 stations' "Tiësto" and
/// "KOMM NÄHER" were fine. Corruption per station, not at random.
///
/// <para>RadioEngine and StreamHarvester each carried their own copy of this code, with the same
/// two bugs. Since the DJ mix plays harvested songs, the harvester's copy is the one that produced
/// the reported symptom — hence <see cref="IcyTags"/>, one implementation for both.</para>
/// </summary>
public class IcyTagDecodingTests
{
    // --- Which encoding a tag actually is ------------------------------------------------------

    /// <summary>The bug, from real data: 011.fm sends ÿ as the single Latin-1 byte 0xFF.</summary>
    [Fact]
    public void DecodesALatin1Tag()
    {
        var bytes = Encoding.Latin1.GetBytes("Queensrÿche");

        Assert.Equal("Queensrÿche", IcyTags.Decode(bytes));
    }

    /// <summary>And the stations that already worked have to keep working — these four are the
    /// non-ASCII rows that survived in the library.</summary>
    [Theory]
    [InlineData("Tiësto")]
    [InlineData("KOMM NÄHER")]
    [InlineData("Jacaré")]
    [InlineData("Santi & Tuğçe")]   // ğ has no Latin-1 equivalent at all
    public void DecodesAUtf8Tag(string text)
    {
        Assert.Equal(text, IcyTags.Decode(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// The order of the two attempts is load-bearing. UTF-8 must be tried FIRST and strictly:
    /// Latin-1 accepts any byte sequence, so trying it first would decode "Tiësto" as mojibake and
    /// never fail over. Strict UTF-8, by contrast, rejects a lone 0xFF and hands off cleanly.
    /// </summary>
    [Fact]
    public void PrefersUtf8OverLatin1WhenTheBytesAreValidUtf8()
    {
        var utf8 = Encoding.UTF8.GetBytes("Tiësto");

        Assert.Equal("Tiësto", IcyTags.Decode(utf8));
        Assert.NotEqual(Encoding.Latin1.GetString(utf8), IcyTags.Decode(utf8)); // "TiÃ«sto"
    }

    [Theory]
    [InlineData("")]
    [InlineData("StreamTitle='Plain ASCII';")]
    [InlineData("icy-name:Hard Rock Heaven")]
    public void AsciiDecodesIdenticallyEitherWay(string text)
    {
        // Nothing about the fallback may disturb the overwhelming majority of tags.
        Assert.Equal(text, IcyTags.Decode(Encoding.ASCII.GetBytes(text)));
    }

    /// <summary>No byte sequence may throw — this runs on a BASS callback thread, where an
    /// exception takes out metadata handling for the rest of the session.</summary>
    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC3 })]                    // truncated UTF-8 lead byte
    [InlineData(new byte[] { 0x80, 0x80, 0x80 })]        // continuation bytes with no lead
    [InlineData(new byte[] { 0x41, 0xFE, 0x42 })]
    public void NeverThrowsOnAnyByteSequence(byte[] bytes)
    {
        Assert.NotNull(IcyTags.Decode(bytes));
    }

    // --- Walking the multi-string ICY block ----------------------------------------------------

    /// <summary>
    /// The second bug, and the one with teeth. The walk used to advance by
    /// <c>Encoding.UTF8.GetByteCount(decoded)</c> — but one Latin-1 byte decodes to U+FFFD, which
    /// is THREE bytes in UTF-8, so the pointer overshot by two and every later tag was misread.
    /// This block is where <c>icy-name</c> comes from, and <see cref="SongHistoryFilter"/> compares
    /// titles against icy-name to catch a station announcing itself.
    /// </summary>
    [Fact]
    public void ALatin1CharacterDoesNotDesynchroniseTheRestOfTheBlock()
    {
        var tags = WithNativeBlock(
            Latin1("icy-name:Café Radio"),      // é as a single 0xE9
            Ascii("icy-genre:Rock"),
            Ascii("icy-br:128"),
            IcyTags.SplitBlock);

        Assert.Equal(3, tags.Count);
        Assert.Equal("icy-name:Café Radio", tags[0]);
        Assert.Equal("icy-genre:Rock", tags[1]);
        Assert.Equal("icy-br:128", tags[2]);
    }

    [Fact]
    public void ReadsAPlainAsciiBlock()
    {
        var tags = WithNativeBlock(
            Ascii("icy-name:Hard Rock Heaven"), Ascii("icy-br:128"), Ascii("icy-pub:1"),
            IcyTags.SplitBlock);

        Assert.Equal(["icy-name:Hard Rock Heaven", "icy-br:128", "icy-pub:1"], tags);
    }

    [Fact]
    public void ANullPointerIsAnEmptyList()
    {
        Assert.Empty(IcyTags.SplitBlock(IntPtr.Zero));
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>
    /// Lays the strings out in unmanaged memory exactly as BASS does — each null-terminated, the
    /// block closed by a second null — and runs the real pointer walk over it. Worth the
    /// Marshal.AllocHGlobal: the bug being pinned IS the pointer arithmetic, so a test over a
    /// managed byte[] would step around it.
    /// </summary>
    private static IReadOnlyList<string> WithNativeBlock(
        byte[] a, byte[] b, byte[] c, Func<IntPtr, IReadOnlyList<string>> read)
    {
        var block = new List<byte>();
        foreach (var part in new[] { a, b, c })
        {
            block.AddRange(part);
            block.Add(0);
        }
        block.Add(0); // terminating null of the pair

        var bytes = block.ToArray();
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return read(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
