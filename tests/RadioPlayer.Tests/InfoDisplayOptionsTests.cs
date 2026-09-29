using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The note under the info display's port box. It is the only place in the app that tells
/// someone the address to type into their display, and the only place a degraded binding surfaces
/// — a feed that could bind nothing but loopback answers every request this PC makes and none the
/// display makes, which on the display looks like a display problem.
/// </summary>
public class InfoDisplayOptionsTests
{
    private static string Address(int port) =>
        $"http://{Environment.MachineName.ToLowerInvariant()}:{port}{InfoDisplayServer.NowPath}";

    /// <summary>Turned off, it still has to say what turning it on would give you — otherwise the
    /// address only ever appears after the user has committed to opening a port.</summary>
    [Fact]
    public void SwitchedOffItStillShowsTheAddressOnOffer()
    {
        var note = OptionsDialog.DescribeInfoDisplay(enabled: false, port: 8723, live: null);

        Assert.Contains(Address(8723), note);
    }

    /// <summary>A port typed but not saved describes what saving would do. Showing the running
    /// feed's message next to a different port number would be describing the wrong server.</summary>
    [Fact]
    public void AnUnsavedPortDescribesWhatSavingWouldDo()
    {
        var running = new InfoDisplayState(
            Running: true, Port: 8723, Url: Address(8723), ReachableFromNetwork: true, Message: "Serving it.");

        var note = OptionsDialog.DescribeInfoDisplay(enabled: true, port: 9100, live: running);

        Assert.Contains(Address(9100), note);
        Assert.DoesNotContain("Serving it.", note);
    }

    /// <summary>
    /// The case the note exists for. Once the outcome is for the port on screen, it is the whole
    /// truth — including "started, but only this PC can reach it", which reads as success anywhere
    /// a boolean is all that's carried.
    /// </summary>
    [Theory]
    [InlineData(true, "Serving http://pc:8723/api/now")]
    [InlineData(false, "Only this PC can reach it")]
    public void OnceItHasRunOnThisPortItsOwnOutcomeIsTheNote(bool reachable, string message)
    {
        var live = new InfoDisplayState(
            Running: true, Port: 8723, Url: Address(8723), ReachableFromNetwork: reachable, Message: message);

        Assert.Equal(message, OptionsDialog.DescribeInfoDisplay(enabled: true, port: 8723, live: live));
    }

    /// <summary>A failed start is an outcome for that port too — "save to start it" would invite
    /// the user to do again exactly what has just failed.</summary>
    [Fact]
    public void AFailedStartIsReportedRatherThanInvitingARetry()
    {
        var failed = new InfoDisplayState(
            Running: false, Port: 8723, Url: null, ReachableFromNetwork: false,
            Message: "Couldn't start on port 8723: the port is in use.");

        var note = OptionsDialog.DescribeInfoDisplay(enabled: true, port: 8723, live: failed);

        Assert.Equal(failed.Message, note);
    }
}
