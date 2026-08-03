using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The enrichment call already runs once per harvested song, so asking it "is this music at all"
/// costs one extra field. It is the only gate in the pipeline with general world knowledge —
/// <see cref="SongHistoryFilter"/> can only catch shapes someone anticipated.
///
/// <para>The asymmetry is what these tests pin down: a false "no" DELETES a real song, a false
/// "yes" only leaves one bad track in a mix. So every unclear answer has to read as "yes".</para>
/// </summary>
public class SongDistillVerdictTests
{
    private const string Description = "A driving mid-tempo rock track with a bright chorus.";

    private static string Json(string fields) =>
        $$"""{ {{fields}} "description": "{{Description}}", "genres": [], "moods": [], "era": null }""";

    // --- The verdict, when the model gives one ------------------------------------------------

    [Fact]
    public void ReadsAPlainFalse()
    {
        var result = SongLibraryService.ParseDistill(Json("\"is_song\": false,"));

        Assert.NotNull(result);
        Assert.False(result!.IsSong);
    }

    [Fact]
    public void ReadsAPlainTrue()
    {
        Assert.True(SongLibraryService.ParseDistill(Json("\"is_song\": true,"))!.IsSong);
    }

    /// <summary>Models sometimes quote a boolean. A quoted "false" is still a no; a quoted
    /// "true" — or any other word — is a yes.</summary>
    [Theory]
    [InlineData("\"false\"", false)]
    [InlineData("\"False\"", false)]
    [InlineData("\"FALSE\"", false)]
    [InlineData("\"true\"", true)]
    [InlineData("\"maybe\"", true)]
    [InlineData("\"\"", true)]
    public void ReadsAQuotedBoolean(string literal, bool expected)
    {
        Assert.Equal(expected, SongLibraryService.ParseDistill(Json($"\"is_song\": {literal},"))!.IsSong);
    }

    // --- Everything unclear means keep --------------------------------------------------------

    [Theory]
    [InlineData("")]                            // field absent entirely — an older prompt's shape
    [InlineData("\"is_song\": null,")]
    [InlineData("\"is_song\": 0,")]             // a number is not an answer, even a falsy one
    [InlineData("\"is_song\": 1,")]
    [InlineData("\"is_song\": [],")]
    [InlineData("\"is_song\": {\"value\": false},")]
    public void TreatsAnythingUnclearAsASong(string field)
    {
        // Silence must never be read as "no" — this decides whether a file gets deleted.
        Assert.True(SongLibraryService.ParseDistill(Json(field))!.IsSong);
    }

    [Fact]
    public void AFailedCallIsNotAVerdict()
    {
        // Null means "we learned nothing", which must not be confused with "not music". The
        // caller only acts on IsSong == false, so a null result leaves the row alone.
        Assert.Null(SongLibraryService.ParseDistill(null));
        Assert.Null(SongLibraryService.ParseDistill(""));
        Assert.Null(SongLibraryService.ParseDistill("I'm sorry, I can't help with that."));
        Assert.Null(SongLibraryService.ParseDistill("{ not json at all "));
    }

    // --- The verdict survives a missing description -------------------------------------------

    /// <summary>A model that has decided this is an ad break may not bother describing it. That
    /// used to return null — no description, no result — which would have thrown the verdict away
    /// along with it and left the ad in the library.</summary>
    [Fact]
    public void KeepsANoEvenWhenTheModelWroteNoDescription()
    {
        var result = SongLibraryService.ParseDistill("""{ "is_song": false, "genres": [] }""");

        Assert.NotNull(result);
        Assert.False(result!.IsSong);
    }

    [Fact]
    public void StillReturnsNullWhenAYesHasNoDescription()
    {
        // Nothing to store and nothing to act on — the fallback description takes over.
        Assert.Null(SongLibraryService.ParseDistill("""{ "is_song": true, "genres": [] }"""));
        Assert.Null(SongLibraryService.ParseDistill("""{ "description": "   " }"""));
    }

    // --- Description parsing, unchanged by the new field --------------------------------------

    [Fact]
    public void StillParsesTheDescriptionAndFacets()
    {
        var result = SongLibraryService.ParseDistill("""
            ```json
            { "is_song": true, "description": "  Wistful synthpop.  ",
              "genres": ["synthpop"], "moods": ["wistful"], "era": "80s" }
            ```
            """);

        Assert.NotNull(result);
        Assert.Equal("Wistful synthpop.", result!.Description);
        Assert.Contains("synthpop", result.FacetsJson);
        Assert.Contains("80s", result.FacetsJson);
    }

    /// <summary>The default matters: every call site that builds a result without stating a
    /// verdict has to mean "song".</summary>
    [Fact]
    public void DefaultsToSong()
    {
        Assert.True(new SongLibraryService.DistillResult("d", null).IsSong);
    }
}
