using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Services;

// Step 0 of doc/PI-PORT-PLAN.md. Usage: BassProbe [url] [seconds] [--any-channels] [--devbuf=MS]
//   url             default: Radio Paradise AAC 128 (exercises the bass_aac path + ICY metadata)
//   seconds         play this long and exit; omit to play until Enter
//   --any-channels  let BASS open the device with every channel it offers (the default before
//                   the fix below; kept so the difference can be heard again)
//   --devbuf=MS     ALSA device buffer length in ms (BASS's Linux default measured at 40 ms)
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var url = positional.Length > 0 ? positional[0] : "http://stream.radioparadise.com/aac-128";
int? seconds = positional.Length > 1 && int.TryParse(positional[1], out var s) ? s : null;
var anyChannels = args.Contains("--any-channels");
var devBuf = args.Select(a => a.StartsWith("--devbuf=") && int.TryParse(a[9..], out var ms) ? ms : (int?)null)
    .FirstOrDefault(v => v is not null);

Console.WriteLine($"BASS {Bass.Version}, OS {Environment.OSVersion}, arch {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

// The Pi 4's headphone driver (bcm2835) advertises 8 channels, and without Stereo BASS opens
// all 8: the stereo jack then misreads every frame and the result is badly distorted. Stereo is
// harmless on Windows, where the default device is already opened as stereo.
var flags = anyChannels ? DeviceInitFlags.Default : DeviceInitFlags.Stereo;
if (devBuf is int buf)
    Bass.Configure(Configuration.DeviceBufferLength, buf); // must precede Init
Console.WriteLine($"Init flags: {flags}, device buffer: {(devBuf is null ? "BASS default" : $"{devBuf} ms")}");
if (!Bass.Init(-1, 44100, flags))
{
    Console.WriteLine($"Bass.Init failed: {Bass.LastError}");
    return 1;
}

var handle = url.Contains("aac", StringComparison.OrdinalIgnoreCase)
    ? BassAac.CreateStream(url, 0, BassFlags.Default, null)
    : Bass.CreateStream(url, 0, BassFlags.Default, null);
if (handle == 0)
{
    Console.WriteLine($"CreateStream failed: {Bass.LastError}");
    Bass.Free();
    return 2;
}

var info = Bass.ChannelGetInfo(handle);
Console.WriteLine($"Stream: {info.ChannelType}, {info.Frequency} Hz, {info.Channels} ch");
// Shoutcast servers answer with ICY headers; Icecast (Radio Paradise) with plain HTTP ones.
foreach (var type in new[] { TagType.ICY, TagType.HTTP })
{
    var block = IcyTags.ReadBlock(handle, type);
    Console.WriteLine($"{type}:" + (block.Count == 0 ? " (none)" : ""));
    foreach (var tag in block)
        Console.WriteLine($"  {tag}");
}

// Held in a local for the whole run: native BASS keeps only a function pointer, so an inline
// lambda could be collected mid-stream (RadioEngine keeps its syncs in fields for the same reason).
SyncProcedure onMeta = (_, ch, _, _) => Console.WriteLine($"META: {IcyTags.ReadString(ch, TagType.META)}");
Bass.ChannelSetSync(handle, SyncFlags.MetadataReceived, 0, onMeta);
// The first title arrives with the stream, before the sync is attached.
Console.WriteLine($"META: {IcyTags.ReadString(handle, TagType.META)}");

if (!Bass.ChannelPlay(handle))
{
    Console.WriteLine($"ChannelPlay failed: {Bass.LastError}");
    Bass.Free();
    return 3;
}

if (seconds is int n)
{
    Console.WriteLine($"Playing for {n}s.");
    for (var i = 0; i < n; i++)
    {
        Thread.Sleep(1000);
        if (Bass.ChannelIsActive(handle) != PlaybackState.Playing)
            Console.WriteLine($"  t={i + 1}s state={Bass.ChannelIsActive(handle)}");
    }
    Console.WriteLine($"Level at exit: {Bass.ChannelGetLevel(handle):X8} (non-zero = audio flowing)");
}
else
{
    Console.WriteLine("Playing. Press Enter to stop.");
    Console.ReadLine();
}

GC.KeepAlive(onMeta);
Bass.Free();
return 0;
