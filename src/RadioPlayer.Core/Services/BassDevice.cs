using ManagedBass;

namespace RadioPlayer.Services;

/// <summary>
/// Opens BASS's default output device. On Linux it must be forced to stereo with a bigger buffer:
/// the Pi 4's headphone driver advertises 8 channels, BASS opens all of them otherwise, and the
/// stereo jack then plays badly distorted audio (doc/PI-PORT-PLAN.md, "Step 0 finding").
/// Windows keeps its existing bare Init.
/// </summary>
internal static class BassDevice
{
    internal static bool InitDefault()
    {
        if (!OperatingSystem.IsLinux())
            return Bass.Init();
        Bass.Configure(Configuration.DeviceBufferLength, 200); // ms; BASS's Linux default is ~40
        return Bass.Init(-1, 44100, DeviceInitFlags.Stereo);
    }
}
