using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The key used to be a string captured in each service's constructor, and the composition root
/// reads settings once at startup — so pasting a key into the options dialog changed nothing until
/// the app was restarted. Eight services were still holding the value from launch.
/// </summary>
public class ApiKeySourceTests
{
    [Fact]
    public void AKeySetAfterConstructionIsVisibleThroughTheSameInstance()
    {
        // The whole point: services hold the source, not the string.
        var keys = new ApiKeySource();
        Assert.False(keys.IsConfigured);

        keys.Current = "sk-ant-whatever";

        Assert.True(keys.IsConfigured);
        Assert.Equal("sk-ant-whatever", keys.Current);
    }

    [Fact]
    public void ClearingTheKeyTurnsAiBackOff()
    {
        // Saving an empty box is how the dialog turns AI features off — it has to actually land.
        var keys = new ApiKeySource("sk-ant-whatever");

        keys.Current = "";

        Assert.False(keys.IsConfigured);
        Assert.Null(keys.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankMeansNoKeyRatherThanAnEmptyKey(string? key)
    {
        // A whitespace value would otherwise pass IsConfigured and produce a 401 on every call
        // instead of the app quietly behaving as unconfigured.
        var keys = new ApiKeySource(key);

        Assert.False(keys.IsConfigured);
        Assert.Null(keys.Current);
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        // Pasted keys routinely arrive with a trailing newline.
        Assert.Equal("sk-ant-abc", new ApiKeySource("  sk-ant-abc\n").Current);
    }

    [Fact]
    public void APlainStringStillWorksWhereASourceIsExpected()
    {
        // The implicit conversion is what keeps every existing call site — and every test that
        // builds a service with apiKey: "test-key" — compiling unchanged.
        ApiKeySource keys = "sk-ant-abc";

        Assert.True(keys.IsConfigured);
    }

    [Fact]
    public void EachConvertedStringIsItsOwnSource()
    {
        // The conversion makes a NEW source, so a string passed to one service cannot be updated
        // through another. Only a shared instance is shared — which is why the composition root
        // must construct one and pass that same object everywhere.
        ApiKeySource a = "one";
        ApiKeySource b = "one";

        a.Current = "two";

        Assert.Equal("one", b.Current);
    }
}
