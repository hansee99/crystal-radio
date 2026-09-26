# Raspberry Pi port — implementation plan

A step-by-step plan for porting Crystal Radio to a Raspberry Pi with a browser UI. Written to
be executed one step at a time by Claude Code sessions, including ones on smaller models, so it
is deliberately explicit: exact files, exact signatures, exact commands, and a definition of done
you can run. Where the plan says "verbatim", copy code — do not re-write it.

**Target shape.** One platform-neutral core library plus two thin heads:

```
crystal-radio.csproj             WPF head (stays at the repo root; unchanged for users)
src/RadioPlayer.Core/            everything that is not Windows-specific: models, services,
                                 engines, view model, threading abstraction
src/RadioPlayer.Web/             ASP.NET Core + Blazor Server head; runs on the Pi (and on
                                 Windows, for development)
```

The Pi plays audio out of its own ALSA output (3.5 mm / HDMI / USB DAC). The browser is a remote
control — it never receives audio. There is one player per host and any number of browser tabs
looking at it.

---

## 0. Rules for every session

Read [CLAUDE.md](../CLAUDE.md) first. Then:

1. **One step per session.** Do the step, run its *Definition of done*, commit with the message
   given, stop. Do not start the next step in the same session unless asked.
2. **The WPF app stays green at every step.** `dotnet build crystal-radio.sln` and
   `dotnet test tests/RadioPlayer.Tests` must pass before you commit. If a step breaks either and
   you cannot fix it inside the step's scope, stop and report — do not widen the scope.
3. **Mechanical, not creative.** These steps are refactors. Do not rename identifiers, reorder
   members, reformat files, rewrite comments, or "improve" logic you are moving. Move code
   verbatim. The only edits are the ones the step lists.
4. **Verify claims by compiling.** Where the plan says "there should be no other references", run
   the grep it gives; if the grep disagrees with the plan, the grep wins — handle what it finds
   in the same spirit and note the discrepancy in your final report.
5. **Preserve threading semantics.** Every `Dispatcher` site exists to keep BASS callbacks off
   the app's state. `BeginInvoke` becomes `Post`, `Invoke` becomes `Send`; never turn one into a
   direct call.
6. **Do not touch** `Views/`, `Theme.xaml`, the installer, or the build scripts unless the step
   says so.
7. **Never commit an API key**, and never commit `MlAssets/*.onnx`.
8. Commit messages: one line in the imperative, as in `git log`. Append the attribution trailer
   the session's system reminder specifies.

Commands run from the repo root (`C:\Dev\radio-player`) in PowerShell.

---

## Step 0 — Spike: prove BASS plays AAC on the Pi

**Why first.** Everything else is refactoring; the only genuine unknown is whether the BASS stack
works on Linux/arm64 with the AAC add-on. Retire that risk before paying for any refactor.

**Prerequisites (human).**

- A Raspberry Pi 4 or 5 running the **64-bit** Raspberry Pi OS. 32-bit will not work
  — ONNX Runtime and SQLite native packages only ship `linux-arm64`.
- Native libraries from <https://www.un4seen.com/> (same non-commercial licence as the Windows
  DLLs, see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)):
  - `bass24-linux.zip` → `libs/aarch64/libbass.so`
  - `bass_aac24-linux.zip` → `libs/aarch64/libbass_aac.so`

  Place both in a new folder `native/linux-arm64/`. **If the AAC add-on has no aarch64 Linux
  build, stop here and report** — the fallback is swapping the engine to LibVLCSharp behind
  `RadioEngine` (CLAUDE.md, "Decisions"), which is a different plan.
- SSH access to the Pi.

**The target Pi (verified 2026-09-26).** `hans@ras4` — Raspberry Pi 4 Model B (2 GB), Debian 13
"trixie" 64-bit, headless (no PipeWire/PulseAudio; BASS talks to ALSA directly, and ALSA's
`default` is the headphone jack). `libasound2t64`, `libicu76` and `libssl3t64` are already
installed. **`hans` does NOT have passwordless sudo** — an earlier `sudo -n true` check passed only
because the user's own recent sudo was still cached; don't trust that check. `ufw` is active. Since
Step 6, `hans` may run `systemctl stop|start|restart crystal-radio.service` without a password and
nothing else. Commands below use `hans@ras4`.

**SSH from this Windows box — two gotchas.**

- The key (`~/.ssh/id_ed25519`) has a passphrase and is unlocked in the **Windows** ssh-agent.
  Git Bash's `/usr/bin/ssh` cannot reach that agent and fails with "Permission denied
  (publickey)" even though the Pi accepts the key. Always call
  `/c/Windows/System32/OpenSSH/ssh.exe` (from Bash) or plain `ssh` from PowerShell, with
  `-o BatchMode=yes` so a missing agent fails fast instead of prompting.
- `scp -r` of a directory drops the connection. Copy directories as a tar stream instead:
  `tar -cf - <dir> | ssh.exe -o BatchMode=yes hans@ras4 'tar -xf - -C ~'`.

**Files.**

- `tools/BassProbe/BassProbe.csproj`
- `tools/BassProbe/Program.cs`

`BassProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ManagedBass" Version="4.0.2" />
    <PackageReference Include="ManagedBass.Aac" Version="4.0.2" />
  </ItemGroup>
  <!-- Natives per RID: ManagedBass P/Invokes "bass" / "bass_aac"; .NET resolves that to
       bass.dll on Windows and libbass.so / libbass_aac.so on Linux, from the app directory. -->
  <ItemGroup Condition="'$(RuntimeIdentifier)' == 'linux-arm64'">
    <Content Include="..\..\native\linux-arm64\libbass.so" Link="libbass.so" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\native\linux-arm64\libbass_aac.so" Link="libbass_aac.so" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup Condition="'$(RuntimeIdentifier)' != 'linux-arm64'">
    <Content Include="..\..\native\x64\bass.dll" Link="bass.dll" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\native\x64\bass_aac.dll" Link="bass_aac.dll" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`Program.cs` (uses the same `BassAac.CreateStream` overload as `RadioEngine`, line ~254):

```csharp
using ManagedBass;
using ManagedBass.Aac;

// Usage: BassProbe [url]   (default: Radio Paradise AAC 128)
var url = args.Length > 0 ? args[0] : "http://stream.radioparadise.com/aac-128";

Console.WriteLine($"BASS {Bass.Version}");
if (!Bass.Init())
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
    return 2;
}

var icy = Bass.ChannelGetTags(handle, TagType.ICY);
Console.WriteLine($"ICY: {icy ?? "(none)"}");

Bass.ChannelSetSync(handle, SyncFlags.MetadataReceived, 0, (_, ch, _, _) =>
    Console.WriteLine($"META: {Bass.ChannelGetTags(ch, TagType.META)}"));

if (!Bass.ChannelPlay(handle))
{
    Console.WriteLine($"ChannelPlay failed: {Bass.LastError}");
    return 3;
}

Console.WriteLine("Playing. Press Enter to stop.");
Console.ReadLine();
Bass.Free();
return 0;
```

**Instructions.**

1. Create the two files. Add the project to the solution: `dotnet sln add tools/BassProbe`.
2. Verify on Windows first: `dotnet run --project tools/BassProbe`. You must hear audio and see
   `META:` lines as songs change.
3. Publish for the Pi:
   `dotnet publish tools/BassProbe -c Release -r linux-arm64 --self-contained -o build/probe`
4. Copy and run on the Pi:
   ```
   cd build && tar -cf - probe | /c/Windows/System32/OpenSSH/ssh.exe -o BatchMode=yes hans@ras4 'rm -rf ~/probe && tar -xf - -C ~'
   /c/Windows/System32/OpenSSH/ssh.exe -o BatchMode=yes hans@ras4 '~/probe/BassProbe http://stream.radioparadise.com/aac-128 30'
   ```
   On a fresh OS, install ALSA and ICU first. Package names change per release (trixie:
   `libasound2t64 libicu76`; bookworm: `libasound2 libicu72`) —
   `apt-cache search '^libicu[0-9]+$'` lists the right ICU one.
5. If `Bass.Init` fails: `aplay -l` on the Pi, then pick the output in `sudo raspi-config` →
   System Options → Audio. BASS opens the ALSA `default` device.
6. If `CreateStream` fails with `FileFormat`/`Unknown` on the AAC URL but an MP3 URL works
   (`http://stream.radioparadise.com/mp3-128`), `libbass_aac.so` is not loading —
   `ldd ~/probe/libbass_aac.so` and check it is really aarch64 (`file libbass_aac.so`).

**Definition of done.** Audio plays on the Pi from the AAC URL and `META:` lines print.
Record the exact BASS version and OS release in your commit message body.

**Commit.** `Add a BASS probe tool and the linux-arm64 natives`

### Step 0 finding — a bare `Bass.Init()` distorts on the Pi (must carry forward)

The first Pi run passed every metric (stream decoded, titles arrived, non-zero level) and still
sounded **badly distorted**. `/proc/asound/card0/pcm0p/sub0/hw_params` during playback showed why:

| | bare `Bass.Init()` | `Bass.Init(-1, 44100, DeviceInitFlags.Stereo)` + 200 ms device buffer |
| --- | --- | --- |
| `channels` | **8** | 2 |
| `buffer_size` | 1764 frames (40 ms) | 8880 frames (200 ms) |
| heard | distorted | clear (confirmed by ear, 60 s across a song change) |

The Pi 4's headphone driver (bcm2835) advertises 8 channels and BASS opens all of them unless told
otherwise; the stereo jack then misreads every frame. The small buffer was not the audible cause
(CPU 91 % idle, no throttling, no xruns), but 40 ms leaves no margin on a Pi, so raise it too.

**Rule for every BASS output device on Linux:** before `Bass.Init`,
`Bass.Configure(Configuration.DeviceBufferLength, 200)`, and init with
`Bass.Init(-1, 44100, DeviceInitFlags.Stereo)`. Applied in Step 5 (below). The DJ harvest's
`Bass.Init(0)` is the "no sound" device and is unaffected.

Lesson for later verification: **a level meter is not a listening test.** Any step whose
definition of done says "audio plays" on the Pi means a person heard it and it sounded right.

`BassProbe --any-channels` reproduces the broken behaviour; `--devbuf=MS` sets the buffer.

---

## Step 1 — Threading abstraction: replace `System.Windows.Threading.Dispatcher`

**Why.** `Dispatcher` is WPF's; it is the *only* thing the engines and the view model need from
WPF. Abstract it and the whole service layer becomes portable. The abstraction mirrors
`Dispatcher` one-for-one so the migration is find-and-replace.

**Design.**

- `IDispatcher` — `Post` (= `BeginInvoke`), `Send` (= `Invoke`), `InvokeAsync`, `CheckAccess`,
  `HasShutdownStarted`, `Shutdown` (= `InvokeShutdown`), `CreateTimer` (= `DispatcherTimer`).
- `DispatcherContext.Current` — the ambient per-thread dispatcher, replacing
  `Dispatcher.CurrentDispatcher`. Thread-static. A process-wide `Fallback` factory lets the WPF
  head and the tests reproduce today's "every thread lazily gets a dispatcher" behaviour.
- `MessageLoop` — a pure-.NET single-threaded pump (a `BlockingCollection<Action>`), used for the
  DJ harvest thread on every platform and for the web head's player thread.
- `WpfDispatcher` — adapter over the real WPF `Dispatcher`, lives in the WPF head.

All new Core types go in namespace `RadioPlayer.Threading`.

**Files to create.**

`Threading/IDispatcher.cs`:

```csharp
namespace RadioPlayer.Threading;

/// <summary>
/// The single-threaded affinity the engines and view model marshal onto. On the WPF head it
/// wraps the window's Dispatcher; elsewhere it is a <see cref="MessageLoop"/> on a dedicated
/// player thread. Members mirror System.Windows.Threading.Dispatcher so the mapping is
/// mechanical: Post == BeginInvoke, Send == Invoke, Shutdown == InvokeShutdown.
/// </summary>
public interface IDispatcher
{
    bool CheckAccess();
    bool HasShutdownStarted { get; }

    /// <summary>Queue and return immediately (BeginInvoke).</summary>
    void Post(Action action);

    /// <summary>Run inline if already on the thread, else block until it has run (Invoke).</summary>
    void Send(Action action);

    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> func);

    /// <summary>Begin shutting the loop down; queued work still runs.</summary>
    void Shutdown();

    /// <summary>A timer whose Tick fires on this dispatcher's thread. Created stopped.</summary>
    IDispatcherTimer CreateTimer();
}

/// <summary>Mirror of DispatcherTimer's surface.</summary>
public interface IDispatcherTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    event EventHandler? Tick;
    void Start();
    void Stop();
}
```

`Threading/DispatcherContext.cs`:

```csharp
namespace RadioPlayer.Threading;

/// <summary>
/// The dispatcher for the current thread — the replacement for Dispatcher.CurrentDispatcher,
/// which every engine captures in its constructor. A thread gets one either explicitly
/// (<see cref="MessageLoop.InstallOnCurrentThread"/>) or lazily through <see cref="Fallback"/>,
/// which the WPF head sets at startup so the UI thread — and any test thread — behaves exactly
/// as before.
/// </summary>
public static class DispatcherContext
{
    [ThreadStatic] private static IDispatcher? _current;

    /// <summary>Process-wide factory used when a thread has no dispatcher installed. Set once,
    /// at startup, by the head; a head with a single player thread leaves it null so a
    /// construction on the wrong thread fails loudly instead of silently getting its own loop.</summary>
    public static Func<IDispatcher>? Fallback { get; set; }

    public static bool HasCurrent => _current is not null;

    public static IDispatcher Current =>
        _current ??= Fallback?.Invoke()
            ?? throw new InvalidOperationException(
                "No dispatcher on this thread. Construct on the player thread (MessageLoop.InstallOnCurrentThread) " +
                "or set DispatcherContext.Fallback at startup.");

    public static void Install(IDispatcher dispatcher) => _current = dispatcher;
}
```

`Threading/MessageLoop.cs` — copy verbatim:

```csharp
using System.Collections.Concurrent;
using RadioPlayer.Services;

namespace RadioPlayer.Threading;

/// <summary>
/// A single-threaded message pump with Dispatcher semantics and no WPF: the thread that calls
/// <see cref="Run"/> executes every posted action in order until <see cref="Shutdown"/>.
/// An action that throws is logged and the loop carries on — the player must never go silent
/// because one callback misbehaved (CLAUDE.md, "it's a player, not a stream ripper").
/// </summary>
public sealed class MessageLoop : IDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly int _threadId;
    private volatile bool _shutdown;

    private MessageLoop(int threadId) => _threadId = threadId;

    /// <summary>Creates a loop bound to the calling thread and makes it that thread's
    /// <see cref="DispatcherContext.Current"/>. Call this before constructing anything that
    /// captures the current dispatcher, then <see cref="Run"/> once setup is done.</summary>
    public static MessageLoop InstallOnCurrentThread()
    {
        var loop = new MessageLoop(Environment.CurrentManagedThreadId);
        DispatcherContext.Install(loop);
        return loop;
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;
    public bool HasShutdownStarted => _shutdown;

    public void Post(Action action)
    {
        if (_queue.IsAddingCompleted) return; // shut down: drop, like a Dispatcher after InvokeShutdown
        try { _queue.Add(action); } catch (InvalidOperationException) { /* completed between check and add */ }
    }

    public void Send(Action action)
    {
        if (CheckAccess()) { action(); return; }
        InvokeAsync(action).GetAwaiter().GetResult();
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (_queue.IsAddingCompleted) tcs.TrySetCanceled();
        return tcs.Task;
    }

    public void Shutdown()
    {
        _shutdown = true;
        Post(() => _queue.CompleteAdding()); // queued work ahead of this still runs
    }

    /// <summary>Pumps until <see cref="Shutdown"/> has been processed. Must be called on the
    /// thread that installed the loop.</summary>
    public void Run()
    {
        if (!CheckAccess())
            throw new InvalidOperationException("MessageLoop.Run must be called on the thread it was installed on.");
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { AppLog.Error("[MessageLoop] unhandled exception in posted action", ex); }
        }
    }

    public IDispatcherTimer CreateTimer() => new LoopTimer(this);

    /// <summary>System.Threading.Timer that delivers Tick through the loop. A tick that is still
    /// queued when the next one is due is not queued twice.</summary>
    private sealed class LoopTimer : IDispatcherTimer
    {
        private readonly MessageLoop _loop;
        private Timer? _timer;
        private TimeSpan _interval = TimeSpan.FromSeconds(1);
        private int _pending;

        public LoopTimer(MessageLoop loop) => _loop = loop;

        public TimeSpan Interval
        {
            get => _interval;
            set { _interval = value; if (IsEnabled) _timer!.Change(_interval, _interval); }
        }

        public bool IsEnabled => _timer is not null;
        public event EventHandler? Tick;

        public void Start()
        {
            if (IsEnabled) return;
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _pending, 1) == 1) return;
                _loop.Post(() =>
                {
                    Interlocked.Exchange(ref _pending, 0);
                    if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty);
                });
            }, null, _interval, _interval);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
```

`AppLog.Error(string, Exception?)` already exists (`Services/AppLog.cs`, line ~53).

`Threading/WpfDispatcher.cs` (WPF adapter; in Step 3 this file stays in the WPF head):

```csharp
using System.Windows.Threading;

namespace RadioPlayer.Threading;

/// <summary>IDispatcher over the real WPF Dispatcher. The UI thread's dispatcher on the WPF head.</summary>
public sealed class WpfDispatcher : IDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static WpfDispatcher ForCurrentThread() => new(Dispatcher.CurrentDispatcher);

    public bool CheckAccess() => _dispatcher.CheckAccess();
    public bool HasShutdownStarted => _dispatcher.HasShutdownStarted;
    public void Post(Action action) => _dispatcher.BeginInvoke(action);
    public void Send(Action action) => _dispatcher.Invoke(action);
    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
    public Task<T> InvokeAsync<T>(Func<T> func) => _dispatcher.InvokeAsync(func).Task;
    public void Shutdown() => _dispatcher.InvokeShutdown();
    public IDispatcherTimer CreateTimer() => new WpfTimer(_dispatcher);

    private sealed class WpfTimer : IDispatcherTimer
    {
        private readonly DispatcherTimer _timer;
        public WpfTimer(Dispatcher d)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, d);
            _timer.Tick += (s, e) => Tick?.Invoke(this, e);
        }
        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public bool IsEnabled => _timer.IsEnabled;
        public event EventHandler? Tick;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
```

**Files to migrate.** Exactly these, found by
`grep -rlE "System\.Windows\.Threading|Dispatcher" Services ViewModels --include=*.cs`
(minus `SmtcController.cs`, which stays WPF and is *not* changed):

| File | What to change |
| --- | --- |
| `Services/RadioEngine.cs` | field `Dispatcher` → `IDispatcher`; `Dispatcher.CurrentDispatcher` → `DispatcherContext.Current`; `BeginInvoke` → `Post` |
| `Services/LocalPlaybackEngine.cs` | as above, plus `new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = … }` → `_dispatcher.CreateTimer()` then set `Interval`; field type `DispatcherTimer` → `IDispatcherTimer` |
| `Services/StreamRecorder.cs` | field + `CurrentDispatcher` + `BeginInvoke` |
| `Services/StreamHarvester.cs` | constructor parameter `Dispatcher dispatcher` → `IDispatcher dispatcher`; field; `BeginInvoke` → `Post` |
| `Services/DjQueueService.cs` | field + `CurrentDispatcher` + `BeginInvoke` |
| `Services/DjHarvestService.cs` | see below — the only non-trivial one |
| `ViewModels/MainViewModel.cs` | field `System.Windows.Threading.Dispatcher` → `IDispatcher`; `CurrentDispatcher` → `DispatcherContext.Current`; `BeginInvoke` → `Post`; `_djSessionTimer` becomes `IDispatcherTimer?` created with `_dispatcher.CreateTimer()` |

Mapping table (apply everywhere in those files):

| Before | After |
| --- | --- |
| `using System.Windows.Threading;` | `using RadioPlayer.Threading;` |
| `Dispatcher.CurrentDispatcher` | `DispatcherContext.Current` |
| field/param type `Dispatcher` | `IDispatcher` |
| `x.BeginInvoke(() => …)`, `x.BeginInvoke(new Action(…))` | `x.Post(() => …)` |
| `x.Invoke(() => …)` | `x.Send(() => …)` |
| `x.InvokeShutdown()` | `x.Shutdown()` |
| `x.HasShutdownStarted` | unchanged |
| `new DispatcherTimer(<priority>, d) { Interval = t }` | `d.CreateTimer()` then `.Interval = t` (priorities are dropped — none of the sites depend on them) |
| `.Tick += …`, `.Start()`, `.Stop()`, `.IsEnabled` | unchanged |

`DjHarvestService.HarvestThreadMain` currently does `_harvestDispatcher = Dispatcher.CurrentDispatcher;`
after `Bass.Init(0)` and ends with `Dispatcher.Run();`. Replace with:

```csharp
var loop = MessageLoop.InstallOnCurrentThread();   // BEFORE StartHarvester: StreamRecorder's ctor captures DispatcherContext.Current
_harvestDispatcher = loop;
…                                                 // StartHarvester(...) loop, unchanged
_watchdog = loop.CreateTimer();                    // was: new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
_watchdog.Interval = TimeSpan.FromSeconds(30);
…                                                 // Tick handler unchanged
ready.TrySetResult();
loop.Run();                                        // returns once Stop()'s dispatched action calls Shutdown()
```

The field `_harvestDispatcher` becomes `IDispatcher?`, `_watchdog` becomes `IDispatcherTimer?`,
and the `Stop()` path's `dispatcher.InvokeShutdown()` becomes `dispatcher.Shutdown()`. Note that
after this step the harvest thread runs on `MessageLoop` on Windows too — that is intended; it
has no reason to be a WPF thread.

**Install the fallback** so the UI thread and test threads get a `WpfDispatcher` lazily, exactly
like `Dispatcher.CurrentDispatcher` did:

- `App.xaml.cs`: add a static constructor to `App` (or the first line of the existing one):
  `DispatcherContext.Fallback = () => WpfDispatcher.ForCurrentThread();`
- `tests/RadioPlayer.Tests/DispatcherSetup.cs` (new):
  ```csharp
  using System.Runtime.CompilerServices;
  using RadioPlayer.Threading;
  namespace RadioPlayer.Tests;
  internal static class DispatcherSetup
  {
      [ModuleInitializer]
      internal static void Init() => DispatcherContext.Fallback = () => WpfDispatcher.ForCurrentThread();
  }
  ```
  The existing tests that pump a `DispatcherFrame` keep working unchanged: `WpfDispatcher` wraps
  the same `Dispatcher.CurrentDispatcher` they pump.
- `tools/DjHarvest/Program.cs` and `tools/DjQueue/Program.cs` use `Dispatcher` directly; add the
  same `DispatcherContext.Fallback = …` line at the top of each `Program.cs`. Nothing else there
  changes in this step.

**Tests to add** — `tests/RadioPlayer.Tests/MessageLoopTests.cs`, xUnit, four facts:

1. `Post` runs actions in order on the loop thread (`CheckAccess()` true inside the action).
2. `Send` from another thread blocks until the action ran; `Send` from the loop thread runs inline.
3. `CreateTimer` with a 50 ms interval fires `Tick` at least three times in one second, on the loop thread, and stops after `Stop()`.
4. `Shutdown` lets already-queued actions run, then `Run()` returns; `Post` after shutdown is a no-op.

Pattern for each: start a `Thread` that calls `MessageLoop.InstallOnCurrentThread()`, hands the
loop back through a `TaskCompletionSource<MessageLoop>`, and calls `Run()`; the test posts work,
then `Shutdown()` and `Join()`.

**Definition of done.**

```
dotnet build crystal-radio.sln
dotnet test tests/RadioPlayer.Tests
grep -rnE "System\.Windows\.Threading|DispatcherTimer|Dispatcher\.CurrentDispatcher" Services ViewModels --include=*.cs
```

The grep must list **only** `Services/SmtcController.cs` and `Threading/WpfDispatcher.cs`. Then
run the app (`dotnet run`), play a station, and start a DJ session for at least five minutes
(the harvest thread now runs on `MessageLoop`); confirm songs still arrive in the mix.

**Commit.** `Abstract the dispatcher so the engines no longer depend on WPF`

### Step 1 — as implemented (differences from the text above)

The code in `Threading/` is the reference now, not the listings above. What changed and why:

- **`MessageLoop` installs a SynchronizationContext** on its thread. A WPF Dispatcher does this
  while it pumps, so `await` without `ConfigureAwait(false)` resumes on the dispatcher thread.
  `MainViewModel`'s async commands depend on that, and in Step 5 they run on a `MessageLoop` —
  without it they would resume on the thread pool and mutate view-model state off-thread. (The
  harvest code already uses `ConfigureAwait(false)` everywhere, so nothing there changed.)
- **`Send` after shutdown returns instead of hanging**, like `Dispatcher.Invoke` on a shut-down
  dispatcher. The listing's version blocked forever on a task that could never complete — reachable
  from `DjHarvestService.ChangeVibeAsync` racing a `Stop()`.
- **`LoopTimer` drops ticks queued before a `Stop`/restart** (a generation counter), and
  `Start()`/setting `Interval` on a running timer restarts the countdown, both as `DispatcherTimer`
  does.
- `_ = dispatcher.BeginInvoke(…)` (`DjHarvestService` top-up) needed its discard removed: `Post`
  returns void.
- The view model's `_djSessionTimer` was a default `DispatcherTimer` (Background priority); it is now
  `_dispatcher.CreateTimer()` (Normal). Irrelevant for a 30 s session-card refresh.
- The test fallback went into the existing `tests/RadioPlayer.Tests/TestHostSetup.cs` module
  initializer, not a second one. `MessageLoopTests` has nine facts: the four above plus inline
  `Send`, exception propagation through `InvokeAsync`, a throwing action not killing the loop,
  `await` resuming on the loop, and `Send` after shutdown not hanging.
- The definition-of-done grep is too loose — `DispatcherTimer` also matches `IDispatcherTimer`. Use
  `\bDispatcherTimer\b`: `grep -rnE "System\.Windows\.Threading|\bDispatcherTimer\b|Dispatcher\.CurrentDispatcher" Services ViewModels --include=*.cs`.
- **Pre-existing, not fixed here:** `tools/DjQueue` does not compile (`Program.cs` does `foreach`
  over a `CurationResult`; it predates the `SongCurator` API change). It fails identically on `main`.

---

## Step 2 — Secret storage abstraction

**Why.** `SettingsStore` protects the API key with DPAPI (`ProtectedData`), which is Windows-only.

**Files.**

`Services/ISecretProtector.cs` (Core):

```csharp
namespace RadioPlayer.Services;

/// <summary>Protects the API key at rest. What "protected" means is the head's choice: DPAPI on
/// Windows; on a headless Linux box, file permissions (see PlainSecretProtector).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    /// <summary>Null when the blob is unreadable (corrupt, or protected by someone else).</summary>
    string? Unprotect(string protectedValue);
}
```

`Services/PlainSecretProtector.cs` (Core):

```csharp
using System.Text;

namespace RadioPlayer.Services;

/// <summary>
/// Base64 only — NOT encryption. For platforms with no per-user secret store: the settings
/// file is written mode 0600 instead (SettingsStore.Save), which is how most CLI tools keep a
/// token on Linux. Also what the tests use.
/// </summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(string protectedValue)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue)); }
        catch (FormatException) { return null; }
    }
}
```

`Services/DpapiSecretProtector.cs` (WPF head — in Step 3 it stays at the root): move the bodies
of `SettingsStore.Protect` / `Unprotect` verbatim into `Protect` / `Unprotect` here, including
the "encrypted by a different user" catch and the DPAPI comment.

**`SettingsStore` changes.**

- Add `private readonly ISecretProtector _secrets;` and a constructor
  `public SettingsStore(ISecretProtector secrets)`. If a parameterless constructor exists, remove
  it. Today the only construction site is `Views/MainWindow.xaml.cs:53` → pass
  `new DpapiSecretProtector()`; confirm with `grep -rn "new SettingsStore(" --include=*.cs Views tools tests`.
- `Protect(plaintext)` → `_secrets.Protect(plaintext)`; `Unprotect(x)` → `_secrets.Unprotect(x)`.
  Delete the two private static methods and `using System.Security.Cryptography;` if nothing
  else uses it.
- In `Save`, after the file is written, add:
  ```csharp
  if (!OperatingSystem.IsWindows())
      File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
  ```
  (`path` = whatever the existing code calls the settings file path.)

**Definition of done.** Build + tests green. `grep -rn "ProtectedData" Services ViewModels`
lists only `Services/DpapiSecretProtector.cs`. Run the app, open Options, save a key, restart,
confirm the key is still there (Options shows it as set / AI search works).

**Commit.** `Put the API-key protection behind an interface`

### Step 2 — as implemented (differences from the text above)

- **The Linux file is never world-readable, even briefly.** The listing chmods *after* writing,
  which leaves the key readable by other users for a moment. `Save` instead creates the file with
  `UnixCreateMode = 0600`, and narrows an existing file *before* writing the key into it.
- `SettingsStore` keeps the "blank stored value = no key" check, so protectors only ever see a
  real value. `System.Text` and `System.Security.Cryptography` usings went with the DPAPI code.
- `SecretProtectorTests` (6 cases) includes one that matters for existing installs: a key stored
  byte-for-byte the way the old `SettingsStore` did it still decrypts through `DpapiSecretProtector`.
- **Verified on ras4**, not just reasoned about: a scratch console app linking the real
  `SettingsStore.cs` ran against `XDG_CONFIG_HOME=/tmp/…` (so `~/.config` was untouched) and
  confirmed a new file is 0600, a pre-existing 0644 file is narrowed to 0600, the key round-trips,
  other settings survive a key change, the key is not stored in the clear, and clearing works.
  That same run showed `SettingsStore.cs` already compiles on plain `net10.0` — needing only
  `IDjIntroService.cs` for `DjPersonality` — which is Step 3's premise.

---

## Step 3 — Split into `RadioPlayer.Core` + the WPF head

**Why.** With Steps 1–2 done, nothing under `Services/`, `Models/`, `ViewModels/`, `Mvvm/` or
`Threading/` needs WPF except the four files listed below. Moving the rest into a `net10.0` class
library is what makes that a guarantee — the compiler enforces it from now on.

**Layout after this step.**

```
src/RadioPlayer.Core/RadioPlayer.Core.csproj
src/RadioPlayer.Core/Models/        ← all of Models/
src/RadioPlayer.Core/Services/      ← all of Services/ EXCEPT SmtcController.cs,
                                      WindowsNotificationService.cs, StartMenuShortcut.cs,
                                      DpapiSecretProtector.cs
src/RadioPlayer.Core/ViewModels/    ← all of ViewModels/
src/RadioPlayer.Core/Mvvm/          ← ObservableObject.cs, RelayCommand.cs
src/RadioPlayer.Core/Threading/     ← IDispatcher.cs, DispatcherContext.cs, MessageLoop.cs

(repo root, WPF head — unchanged paths)
Services/SmtcController.cs, Services/WindowsNotificationService.cs,
Services/StartMenuShortcut.cs, Services/DpapiSecretProtector.cs
Mvvm/UrlIsPlayingMultiConverter.cs
Threading/WpfDispatcher.cs
Views/, Controls/, App.xaml(.cs), AssemblyInfo.cs, app.manifest, Assets/, Fonts/, native/, MlAssets/
```

Namespaces do **not** change (`RootNamespace` is `RadioPlayer` in both projects), so no `using`
lines change. Use `git mv` for every move so history follows the files.

**`src/RadioPlayer.Core/RadioPlayer.Core.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>RadioPlayer</RootNamespace>
    <AssemblyName>RadioPlayer.Core</AssemblyName>
  </PropertyGroup>

  <!-- MusicDetector/Fft are internal; the tests exercise them directly (see the note that
       used to live in crystal-radio.csproj). -->
  <ItemGroup>
    <InternalsVisibleTo Include="RadioPlayer.Tests" />
  </ItemGroup>

  <ItemGroup>
    <!-- Move these PackageReference lines verbatim from crystal-radio.csproj, INCLUDING the
         SQLitePCLRaw pin and its comment. -->
  </ItemGroup>

</Project>
```

Move every `<PackageReference>` from `crystal-radio.csproj` into Core (ManagedBass,
ManagedBass.Aac, Microsoft.Data.Sqlite, SQLitePCLRaw.bundle_e_sqlite3 with its comment,
Microsoft.ML.OnnxRuntime, Microsoft.ML.Tokenizers). The WPF project gets them transitively.

**`crystal-radio.csproj` changes.**

- `DefaultItemExcludes`: add `src\**` → `$(DefaultItemExcludes);tools\**;tests\**;src\**`.
- Remove the `<InternalsVisibleTo>` item group (moved to Core).
- Remove the moved `<PackageReference>`s.
- Add `<ItemGroup><ProjectReference Include="src\RadioPlayer.Core\RadioPlayer.Core.csproj" /></ItemGroup>`.
- Keep all `Resource` and `Content` items (fonts, icons, natives, MlAssets) exactly as they are —
  Content does not flow across a ProjectReference, so each head copies its own natives.

**Other projects.**

- `dotnet sln crystal-radio.sln add src/RadioPlayer.Core/RadioPlayer.Core.csproj`.
- `tools/DjHarvest/DjHarvest.csproj` and `tools/DjDetector/DjDetector.csproj` link
  `..\..\Services\Fft.cs` and `..\..\Services\MusicDetector.cs` as source — change both paths to
  `..\..\src\RadioPlayer.Core\Services\…`.
- Tests and the other tools keep referencing `crystal-radio.csproj` (they get Core transitively,
  and DjHarvest/DjQueue still use the WPF `Dispatcher` in their `Program.cs`). No change.
- `scripts/*.ps1` reference only `crystal-radio.csproj` and the test project — no change. Confirm
  with `grep -nE "Services|ViewModels|Models" scripts/*.ps1` (expect no hits).

**Docs to update in this step** (paths only, no prose rewrites):

- `doc/TECHNICAL.md` "Project structure" table: prefix moved paths with `src/RadioPlayer.Core/`.
- `CLAUDE.md` "Architecture": add one paragraph — *"The service layer, models, view model and the
  threading abstraction live in `src/RadioPlayer.Core` (plain `net10.0`, no WPF). The repo-root
  project is the WPF head: views, SMTC, toasts, the Start Menu shortcut, `WpfDispatcher`,
  `DpapiSecretProtector`. A second head, `src/RadioPlayer.Web`, targets the Raspberry Pi — see
  `doc/PI-PORT-PLAN.md`."*

**Definition of done.**

```
dotnet build crystal-radio.sln
dotnet test tests/RadioPlayer.Tests
dotnet build tools/DjHarvest; dotnet build tools/DjQueue; dotnet build tools/SeedEnrichment; dotnet build tools/DjDetector
grep -rn "System.Windows" src/RadioPlayer.Core   # must print nothing
git status --porcelain | grep -v "^R"            # moves should show as renames; review anything that isn't
```

Run the app and play a station. Run `.\scripts\build-release.ps1` (or at minimum
`dotnet publish crystal-radio.csproj -c Release -r win-x64`) and confirm `bass.dll`,
`bass_aac.dll` and `MlAssets\` are in the publish output.

**Commit.** `Split the platform-neutral core out of the WPF project`

### Step 3 — as implemented (differences from the text above)

- **`InternalsVisibleTo RadioPlayer.Tests` is in both projects**, not moved. Tests also reach
  internals that stay in the head (`ListCentering`, `OptionsDialog`, `WindowsNotificationService`).
- **Versioning.** `src/Directory.Build.props` reads `<Version>` out of `crystal-radio.csproj`
  (a regex at evaluation time), so every project under `src/` reports the product version while
  the release scripts keep their single source. Needed because `LyricsService` builds LRCLIB's
  User-Agent from its *own* assembly version and would otherwise have reported Core's 1.0.0.
  `LyricsServiceTests` now compares against the head's (`App`) version, so it catches this.
- **`DjHarvestService` called `Thread.SetApartmentState(STA)`** — a leftover from the WPF
  Dispatcher days, and it throws `PlatformNotSupportedException` on Linux: DJ mode would have died
  on the Pi at session start. Now Windows-only. Found by CA1416 once Core targeted `net10.0` —
  exactly what the split is for. Build Core with `--no-incremental` to see all CA1416 warnings.
- `tools/BassProbe` links `IcyTags.cs` and needed its path updated too.
- Doc paths updated in `CLAUDE.md`, `doc/TECHNICAL.md` and the tool READMEs. The DJ-mode spec
  docs were left alone: they are historical proposals (several files they name never existed).
- **Verified:** `dotnet publish -r win-x64` of this branch and of `main` differ by exactly
  `RadioPlayer.Core.dll`/`.pdb` (26 → 28 files), both DLLs report 1.10.0, 707 tests pass, the
  app starts cleanly.

---

## Step 4 — Shared composition root

**Why.** `Views/MainWindow.xaml.cs` lines ~53–128 construct every service and the view model in a
specific order with specific `Stage(...)` splash messages, and `OnClosed` disposes them in a
specific order. The web head needs the identical graph. Move it into Core once so there is one
copy.

**File.** `src/RadioPlayer.Core/Services/AppServices.cs`:

```csharp
using RadioPlayer.ViewModels;

namespace RadioPlayer.Services;

/// <summary>What a head must supply: the four things that differ between a desktop window and
/// a browser.</summary>
public sealed record AppHooks(
    ISecretProtector Secrets,
    INotificationService Notifications,
    IStationDialog StationDialog,
    IConfirmDialog ConfirmDialog);

/// <summary>
/// The object graph, built once per process on the player thread (DispatcherContext.Current is
/// captured by the engines' constructors). Construction order and the Stage messages are the
/// ones MainWindow used; Dispose order is the one its OnClosed used — both matter (BASS device
/// ownership, harvest thread teardown), so keep them.
/// </summary>
public sealed class AppServices : IDisposable
{
    public required RadioEngine Engine { get; init; }
    public required LocalPlaybackEngine LocalEngine { get; init; }
    public required StreamRecorder Recorder { get; init; }
    public required SettingsStore Settings { get; init; }
    public required ApiKeySource ApiKeys { get; init; }
    public required EnrichmentStore EnrichmentStore { get; init; }
    public required LibraryStore LibraryStore { get; init; }
    public required MiniLmEmbeddingProvider EmbeddingProvider { get; init; }
    public required DjHarvestService DjHarvest { get; init; }
    public required DjIntroService DjIntro { get; init; }
    public required MainViewModel ViewModel { get; init; }
    // Add a property for every other field MainWindow still reads after construction.

    /// <param name="stage">Progress messages for a splash screen; the web head passes null.</param>
    public static AppServices Build(AppHooks hooks, Action<string>? stage = null)
    {
        void Stage(string s) => stage?.Invoke(s);
        // Body: MainWindow.xaml.cs construction block, moved verbatim, with:
        //   new SettingsStore(new DpapiSecretProtector())  → new SettingsStore(hooks.Secrets)
        //   new WindowsNotificationService()               → hooks.Notifications
        //   new StationDialogService(this)                 → hooks.StationDialog
        //   new ConfirmDialogService(this)                 → hooks.ConfirmDialog
        // and every `_field = …` becoming a local that feeds the object initializer below.
        …
        return new AppServices { … };
    }

    public void Dispose()
    {
        // MainWindow.OnClosed body, verbatim order and comments, minus _smtc (that is the head's).
    }
}
```

`ResolveApiKey()` (settings first, then `ANTHROPIC_API_KEY`) moves here too, as a private static.

**`MainWindow.xaml.cs` changes.** Replace the construction block with

```csharp
_services = AppServices.Build(
    new AppHooks(new DpapiSecretProtector(), new WindowsNotificationService(),
                 new StationDialogService(this), new ConfirmDialogService(this)),
    Stage);
_viewModel = _services.ViewModel;
```

and every later `_engine` / `_recorder` / `_djHarvest` / … read with `_services.Engine` etc.
`OnClosed` becomes: `_smtc?.Dispose();` in its existing position relative to the others is what
matters — it disposed *after* `_djHarvest` and *before* `_localEngine`. Keep that by having
`AppServices.Dispose` take an optional `Action? beforeEngines` hook, **or** simpler: in
`OnClosed`, call `_services.DjHarvest.Dispose()`, then `_smtc?.Dispose()`, then
`_services.Dispose()`. `AppServices.Dispose` disposing `DjHarvest` a second time is safe:
`Dispose() => Stop()`, and `Stop` returns immediately when `_running` is false.

Delete the now-unused fields from `MainWindow`.

**Definition of done.** Build + tests green. Run the app: splash messages appear as before, a
station plays, DJ mode starts, closing the window exits cleanly with no exception in the log
(`%LocalAppData%\RadioPlayer\` log file — find its name in `Services/AppLog.cs`).

**Commit.** `Move the composition root into Core so both heads build the same graph`

### Step 4 — as implemented (differences from the text above)

- **`ApplySettings()` and `ResolveApiKey` moved into `AppServices` too.** Pushing saved settings
  into running services is not view logic, and the web head's Settings page needs the same call.
  `MainWindow.Options_Click` now calls `_services.ApplySettings()`.
- **Teardown is `Shutdown(Action? detachOsIntegration)`**, not `Dispose` plus a double-dispose.
  The callback runs exactly where `OnClosed` used to dispose SMTC — after the DJ harvest (stopping
  it can still push now-playing through the view model into SMTC), before the engines. It also
  includes `ViewModel.SaveSettings()`, which `OnClosed` did first. `Dispose()` = `Shutdown()`;
  both are idempotent.
- `enrichment.BackfillEmbeddingsInBackground()` runs at the end of `Build`, not after the view
  wiring — it is a background task with no dependency on it.
- `AppServices` exposes only what a head reads after construction (`Settings`, `Engine`,
  `ViewModel`, …); the rest stay locals inside `Build`.
- Verified the move mechanically: the old construction block (from `HEAD`) and `Build`, after
  normalising `_field` → local names, differ only in the four hook substitutions and the
  `ApiKeySource` local.

---

## Step 5 — The web head (Blazor Server), verified on Windows first

**Why Blazor Server.** C# end to end, component state bound to the same `MainViewModel` the WPF
window binds to, and server-push of now-playing changes comes free with the SignalR circuit. The
appliance is single-user on a LAN, so one circuit per open tab is fine.

**The one new idea — the player thread.** In WPF the UI thread owns the view model. Here a
dedicated thread running a `MessageLoop` owns it, for the life of the process; browser tabs are
observers. Every read of, and every call into, the view model from a Blazor component goes
through `PlayerHost.InvokeAsync`. Do not read view-model properties directly from a component.

**First: Linux audio device init** (the Step 0 finding). `RadioEngine` (ctor, ~line 70) and
`LocalPlaybackEngine` (ctor, ~line 101) both call a bare `Bass.Init()`. Add one helper in Core,
`Services/BassDevice.cs`, and call it from both instead:

```csharp
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
```

Keep each call site's existing `&& Bass.LastError != Errors.Already` check exactly as it is:
`if (!BassDevice.InitDefault() && Bass.LastError != Errors.Already)`. Do not touch
`DjHarvestService`'s `Bass.Init(0)`.

**Project.** `dotnet new blazor -n RadioPlayer.Web -o src/RadioPlayer.Web --interactivity Server --empty`
then edit the csproj to:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>RadioPlayer.Web</RootNamespace>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\RadioPlayer.Core\RadioPlayer.Core.csproj" />
  </ItemGroup>

  <!-- Natives per RID (Content does not flow across a ProjectReference). The Windows set lets
       the web head run on the dev box; the Linux set is what ships to the Pi. -->
  <ItemGroup Condition="'$(RuntimeIdentifier)' == 'linux-arm64'">
    <Content Include="..\..\native\linux-arm64\libbass.so" Link="libbass.so" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\native\linux-arm64\libbass_aac.so" Link="libbass_aac.so" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup Condition="'$(RuntimeIdentifier)' != 'linux-arm64'">
    <Content Include="..\..\native\x64\bass.dll" Link="bass.dll" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\native\x64\bass_aac.dll" Link="bass_aac.dll" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <!-- Embedding model + vocab, same as the WPF head. -->
  <ItemGroup>
    <Content Include="..\..\MlAssets\all-MiniLM-L6-v2.onnx" Link="MlAssets\all-MiniLM-L6-v2.onnx" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="..\..\MlAssets\vocab.txt" Link="MlAssets\vocab.txt" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`dotnet sln crystal-radio.sln add src/RadioPlayer.Web/RadioPlayer.Web.csproj`.

**`Hosting/PlayerHost.cs`** — the singleton that owns the thread, the loop and the graph:

```csharp
using RadioPlayer.Services;
using RadioPlayer.Threading;
using RadioPlayer.ViewModels;

namespace RadioPlayer.Web.Hosting;

/// <summary>
/// Owns the player thread. The whole object graph is built on it (the engines capture
/// DispatcherContext.Current in their constructors) and lives until the host stops. Components
/// never touch the view model directly: they InvokeAsync onto this thread and get a snapshot back.
/// </summary>
public sealed class PlayerHost : IHostedService
{
    private readonly ILogger<PlayerHost> _log;
    private Thread? _thread;
    private MessageLoop? _loop;
    private AppServices? _services;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlayerHost(ILogger<PlayerHost> log) => _log = log;

    /// <summary>Raised on the player thread whenever any view-model property or watched
    /// collection changes. Components re-read their snapshot on it.</summary>
    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(ThreadMain) { Name = "Player", IsBackground = true };
        _thread.Start();
        return _ready.Task;
    }

    private void ThreadMain()
    {
        try
        {
            _loop = MessageLoop.InstallOnCurrentThread();
            _services = AppServices.Build(new AppHooks(
                new PlainSecretProtector(), new NoNotificationService(),
                new NoStationDialog(), new AlwaysConfirmDialog()));
            var vm = _services.ViewModel;
            vm.PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            vm.Stations.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            vm.SearchResults.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            // Later pages add History, RecentOnStation, CuratedQueue, LibrarySongs, DjHarvesters, DjMix.
            _ready.SetResult();
            _loop.Run();
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "player thread died");
            _ready.TrySetException(ex);
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_loop is null) return Task.CompletedTask;
        _loop.Post(() => _services?.Dispose());
        _loop.Shutdown();
        _thread?.Join(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public Task<T> ReadAsync<T>(Func<MainViewModel, T> read) => _loop!.InvokeAsync(() => read(_services!.ViewModel));
    public Task DoAsync(Action<MainViewModel> act) => _loop!.InvokeAsync(() => act(_services!.ViewModel));
}
```

Plus three trivial hook implementations in `Hosting/`:

- `NoNotificationService : INotificationService` — `IsAvailable => false`, `ShowDjTrack` no-op.
- `NoStationDialog : IStationDialog` — `Show(existing) => null` (cancel). The web UI edits
  stations through its own form (below), not through this.
- `AlwaysConfirmDialog : IConfirmDialog` — returns `new ConfirmResult(true)`. The web UI asks the
  user itself (a JS `confirm`) *before* invoking the command, so by the time the view model asks,
  the answer is yes. Blocking the player thread on a browser round-trip is not acceptable.

**`Program.cs`:**

```csharp
using RadioPlayer.Web.Components;
using RadioPlayer.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<PlayerHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PlayerHost>());

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
```

Set the listen address by environment (`ASPNETCORE_URLS=http://0.0.0.0:5000`), not in code. No
authentication: this is a LAN appliance. Say so in a comment at the top of `Program.cs`.

**Station add/edit without a dialog.** `MainViewModel`'s add/edit commands call
`IStationDialog.Show(...)` and then apply the result. Read those two command handlers, and
extract the "apply the result" part into a public method on the view model —
`public void CommitStation(Station? existing, Station edited)` — that both the command handlers
and the web form call. Same rule as everywhere: move code, do not rewrite it. Add a unit test
that `CommitStation(null, s)` appends and `CommitStation(a, b)` replaces in place.

**Pages (MVP).** A single `Pages/Home.razor` with these regions, plus `Pages/Settings.razor`:

- **Now playing** — station name, title, artist, `PlaybackState`, buffering/reconnecting text.
- **Transport** — play/pause, stop, previous, next station; volume slider (0–100 → `Volume`
  0.0–1.0).
- **Stations** — the list; click plays; add form (name, url, format) → `CommitStation`; delete
  with `confirm()`.
- **Search** — a text box → the view model's search command; results list; click plays.
- **Settings** — API key (masked input) → `SettingsStore.SetApiKey` via `DoAsync`; shows whether
  a key is set and whether `ANTHROPIC_API_KEY` is set in the environment.

Component pattern — copy this shape for every region:

```razor
@implements IDisposable
@inject PlayerHost Player

<div class="now-playing">
    <div class="station">@_s.Station</div>
    <div class="title">@_s.Title</div>
    <div class="artist">@_s.Artist</div>
    <div class="state">@_s.State</div>
</div>

@code {
    private record Snapshot(string Station, string Title, string Artist, string State);
    private Snapshot _s = new("", "", "", "");

    protected override async Task OnInitializedAsync()
    {
        Player.Changed += OnChanged;
        await Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) => _ = InvokeAsync(Refresh);

    private async Task Refresh()
    {
        // These are MainViewModel's real property names (grep before adding more).
        _s = await Player.ReadAsync(vm => new Snapshot(
            vm.NowPlayingStation, vm.NowPlayingTitle, vm.NowPlayingArtist, vm.StatusText));
        StateHasChanged();
    }

    public void Dispose() => Player.Changed -= OnChanged;
}
```

Commands: `await Player.DoAsync(vm => vm.PlayPauseCommand.Execute(null));`. The commands and
properties the MVP needs, as they exist on `MainViewModel` today:

| Purpose | Member |
| --- | --- |
| transport | `PlayPauseCommand`, `StopCommand`, `NextStationCommand`, `PrevStationCommand`, `IsPlaying`, `Volume` (0.0–1.0) |
| now playing | `NowPlayingStation`, `NowPlayingTitle`, `NowPlayingArtist`, `NowPlayingFormat`, `StatusText` |
| stations | `Stations` (ObservableCollection<Station>), `SelectedStation` (setting it plays), `AddStationCommand`, `EditStationCommand`, `DeleteStationCommand` |
| search | `SearchPrompt`, `SearchCommand`, `IsSearching`, `SearchResults` (ObservableCollection<SearchResultItem>), `AddSearchResultCommand` |
| mode | `Mode` (PlayerMode), `SwitchToRadioCommand` / `SwitchToLibraryCommand` / `SwitchToDjCommand` — not needed for the MVP |

Other collections `PlayerHost` may need to watch later: `History`, `RecentOnStation`,
`CuratedQueue`, `LibrarySongs`, `DjHarvesters`, `DjMix`.
Collections: build them inside `ReadAsync` (`vm.Stations.Select(s => new Row(...)).ToList()`),
never hand an `ObservableCollection` to the render tree.

`Changed` can fire many times per second during buffering; if rendering gets chatty, debounce in
`OnChanged` with a 100 ms `Timer` before calling `Refresh`. Not needed for the MVP unless observed.

Styling: one `wwwroot/app.css`, phone-width first, dark. Do not spend effort on it in this step.

**Definition of done (Windows).**

```
dotnet build crystal-radio.sln
dotnet test tests/RadioPlayer.Tests
dotnet run --project src/RadioPlayer.Web
```

In a browser at `http://localhost:5000`: a station plays through the dev box's speakers, the
title updates when the song changes (open a second tab — it updates there too), play/pause and
volume work, adding and deleting a station persists across a restart, a search returns results
and clicking one plays it, saving an API key in Settings makes search work after a restart. The
WPF app still runs unchanged. The two heads share the same `%AppData%\RadioPlayer` data, so run
one at a time.

**Commit.** `Add a Blazor Server head that drives the player from a browser`

### Step 5 — as implemented (differences from the text above)

- **No `CommitStation` refactor — the view model is untouched.** `WebStationDialog` answers the
  add/edit commands' `IStationDialog.Show()` in advance: `Supply(station, run)` sets the station,
  runs `AddStationCommand`, and the command's own `Show()` call receives it — all inside one
  `DoAsync`, so nothing interleaves. Same bookkeeping as the desktop, zero risk to it.
- **`PlayerHost` exposes `AppServices`, not just the view model** (`ReadAsync(Func<AppServices,T>)`,
  `DoAsync(Action<AppServices>)`): Settings needs `SettingsStore` + `ApplySettings()`, and
  tap-to-play needs `Engine.CurrentStation`.
- **Change notifications are coalesced** on the player thread: a burst of property changes inside
  one piece of work (a station switch touches a dozen) raises one `Changed` once that work is done.
  `PlayerHost.Watch` lists exactly what is observed — the view model, `Stations`, `SearchResults`,
  `SearchProgress` and its `Stages`, plus the rows inside them. Add to it when a page reads more.
- **`PlayerView<TSnapshot>`** (`Components/PlayerView.cs`) is the base for every live component:
  subscribe, snapshot on the player thread, one queued refresh at most per component.
- **Tap-to-play:** setting `SelectedStation` already switches station when something is on air, so
  `PlaySelectedStation()` is called only if `Engine.CurrentStation` isn't the tapped station
  afterwards — otherwise the stream would be opened twice. `Play()` sets `CurrentStation`
  synchronously, so the check is reliable. (`SelectedSearchResult`'s setter does not auto-play.)
- `App.razor` sets `@rendermode="InteractiveServer"` on `Routes` and `HeadOutlet`, so every page is
  interactive. The template's HTTPS redirection and HSTS were removed (LAN appliance, no cert).
- **Gotcha — a non-published build must run as Development.** `dotnet run --no-launch-profile`
  without `ASPNETCORE_ENVIRONMENT=Development` comes up as Production, doesn't load the static web
  assets manifest, and `_framework/blazor.web.js` plus the scoped CSS return **500** — the page
  renders but nothing is interactive. The launch profile sets Development; a published build copies
  the assets into `wwwroot/` and works in Production (verified: `dotnet publish -r win-x64`, run in
  Production, every asset 200).
- **Verified by hand in a browser:** play/stop/prev/next/volume, a second tab following along,
  search with progress stages, play and add a result, add a station by URL and remove it, and saving
  the API key — all working, no warnings in either log. 707 tests pass.

---

## Step 6 — Deploy to the Pi

**Files.**

- `scripts/publish-pi.ps1` — wraps the publish and prints the `scp` line:
  ```powershell
  dotnet publish (Join-Path $repo 'src\RadioPlayer.Web') -c Release -r linux-arm64 --self-contained -o (Join-Path $repo 'build\pi')
  ```
  Follow the style of `scripts/build-release.ps1` (repo-root discovery, `Fail` helper).
- `deploy/pi/crystal-radio.service`:
  ```ini
  [Unit]
  Description=Crystal Radio
  After=network-online.target sound.target
  Wants=network-online.target

  [Service]
  User=hans
  WorkingDirectory=/home/hans/crystal-radio
  ExecStart=/home/hans/crystal-radio/RadioPlayer.Web
  Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
  Environment=DOTNET_gcServer=0
  # Optional: Environment=ANTHROPIC_API_KEY=...   (or set it once in the web UI's Settings page)
  Restart=on-failure
  RestartSec=5

  [Install]
  WantedBy=multi-user.target
  ```
- `deploy/pi/README.md` with the steps below.

**Steps on the Pi (document them in that README):**

```
# from the Windows box (see Step 0 for why tar + Windows ssh.exe):
cd build && tar -cf - pi | /c/Windows/System32/OpenSSH/ssh.exe -o BatchMode=yes hans@ras4 'rm -rf ~/crystal-radio && mkdir ~/crystal-radio && tar -xf - -C ~/crystal-radio --strip-components=1'

# on the Pi:
sudo apt-get install -y libasound2t64 libicu76     # already present on ras4; names per Step 0
chmod +x ~/crystal-radio/RadioPlayer.Web
sudo raspi-config    # System Options → Audio → pick headphone / HDMI / USB DAC
sudo cp ~/crystal-radio/deploy/crystal-radio.service /etc/systemd/system/   # (copy the unit file up separately)
sudo systemctl daemon-reload && sudo systemctl enable --now crystal-radio
journalctl -u crystal-radio -f
```

Data lands under `~/.local/share/RadioPlayer` (`LocalApplicationData`) and `~/.config/RadioPlayer`
(`ApplicationData`) — the same folder names the Windows build uses, via `Environment.GetFolderPath`.
Nothing in Core hardcodes a Windows path; confirm with `grep -rn "C:\\\\\|%AppData%\|\\\\\\\\" src/RadioPlayer.Core --include=*.cs`
(expect only comments).

**Definition of done.** From another machine on the LAN, `http://ras4:5000` shows the UI,
a station plays out of the Pi, the title updates, and `sudo reboot` brings the service back up
by itself. Then update the docs:

- `README.md`: a "Raspberry Pi" section under "Quick start" linking to `deploy/pi/README.md`.
- `CLAUDE.md` "Tech stack": add the web head and the Pi target in two lines.
- `THIRD-PARTY-NOTICES.md`: the Linux BASS libraries, same licence.

**Commit.** `Publish the web head to a Raspberry Pi as a systemd service`

### Step 6 — as implemented (differences from the text above)

`deploy/pi/README.md` is the user-facing guide now; this records what differs from the plan above.

- **Root work is split out into `setup.sh`, run once by a person** (`ssh -t hans@ras4 sudo bash
  ~/crystal-radio/deploy/setup.sh`) — there is no passwordless sudo on the Pi. It installs and
  enables the unit, opens **ufw** port 5000 for the LAN subnet only, and adds
  `/etc/sudoers.d/crystal-radio` allowing `systemctl stop|start|restart crystal-radio.service`
  without a password. Updates (`install.sh`) then need no password at all.
- **`scripts/publish-pi.ps1 -Deploy` publishes and deploys in one step.** One tar stream (app as
  `pi/` + `install.sh`, `setup.sh`, the unit) goes to `~/crystal-radio.new`, and `install.sh` swaps
  it in. Exit code 10 from `install.sh` means "first install: run setup.sh". Pitfalls hit while
  writing it: the `tar | ssh` pipe must run through `cmd.exe` (Windows PowerShell corrupts binary
  data piped between native programs); `$deploy` collided with the `-Deploy` switch (PowerShell
  variable names are case-insensitive); and the file needs a BOM *and* ASCII-only text.
- **`.gitattributes` forces LF** for `deploy/pi/*.sh` and `*.service` — this repo checks out CRLF
  (`core.autocrlf=true`), which breaks bash and systemd.
- **Bug found on the Pi: `GetFolderPath` returned `""`.** `~/.local` didn't exist on a fresh image,
  and without `SpecialFolderOption.Create` .NET returns an empty string for a missing folder, so
  `enrichment.db`, `library.db` and the logs were written *relative to the working directory* —
  inside `~/crystal-radio`, which every update deletes. All 10 calls in Core now pass `Create`
  (identical on Windows, where the folders exist), and `SpecialFolderUsageTests` scans Core's source
  and fails on any call without it (verified: it lists the offenders when the fix is reverted).
- **Verified on ras4:** runs directly in ~4 s; SIGTERM gives "Application is shutting down…" and
  exit 0 (so `systemctl stop` is clean); data under `~/.config` and `~/.local/share`; the service
  restarts through the sudo rule in ~4 s; `http://ras4:5000` answers from Windows across the LAN;
  searching and playing a station works on the Pi (confirmed by ear). The unit adds
  `TimeoutStopSec=20` so the DJ harvest has time to stop on shutdown.
- Testing trap: `pgrep -f`/`pkill -f` with the app's path match the ssh command running them —
  signal the PID instead (`$!`).
- Found on the first *update* deploy: (1) Windows PowerShell under `$ErrorActionPreference='Stop'`
  turns any stderr line of a native program into a terminating error, so `curl`'s expected
  "not up yet" messages aborted the script mid-install — the `cmd.exe` call now runs under
  `'Continue'` and is judged by exit code, and `install.sh`'s wait loop is quiet. (2) `ras4`
  resolves only via mDNS here (the router's DNS doesn't know it), which intermittently fails on
  Wi-Fi with "No such host is known"; the reachability check retries 4× before giving up. A DHCP
  reservation plus a router DNS entry (or a hosts-file line) would make the name reliable.

---

## Step 7 — Follow-ups (each is its own session; do in this order)

1. **History and library pages** in the web head — read-only lists first, then the library player
   (`PlayerMode` switch + `LocalPlaybackEngine` transport and seek).
2. **DJ mode page** — start/stop, vibe prompt, the mix list, the session card. The harvest thread
   already runs on `MessageLoop`; nothing in Core changes.
3. **Replace the pre-confirm hack** (`AlwaysConfirmDialog`): make `IConfirmDialog.Ask` async
   (`Task<ConfirmResult>`) so the browser can answer. Touches `MainViewModel`'s callers and
   `Views/ConfirmDialogService.cs`; do it only after 1–2 so the web pages exist to drive it.
4. **MPRIS** (`Tmds.DBus`) so the Pi shows up in phone/desktop media controls — the Linux
   equivalent of `SmtcController`, behind the same `IPlaybackEngine` it uses.
5. **Move the tests off `DispatcherFrame`**: the four tests that pump a frame can run on a
   `MessageLoop` instead, which would let the test project drop `UseWPF` and target plain
   `net10.0`. Low value until someone wants to run the tests on Linux.
6. **Web UI polish** — the Crystal look from `Views/Theme.xaml`, translated to CSS.

### Step 7 — progress (branch `pi-web-pages`)

- **1 — done.** `/history` (latest 200, Save / "Save when it ends" through `SaveSongCommand`) and
  `/library` (play any saved song, AI playlist from a prompt). The player page has a
  Radio | Library | DJ switch and a seek bar. `LibrarySongs` is the user's own saves only —
  DJ-harvested songs share the index but stay out of the list, as on the desktop.
- **2 — done.** `/dj`: one prompt box that starts a session or changes the vibe
  (`DjPromptSubmitCommand`), stop, status and stages, the DJ's remark at `DjRemarkSize`, up next,
  mix, sources. Panel states are the view model's own.
- **3 — done differently, and smaller.** `IConfirmDialog` has exactly one caller, the mode switch.
  Instead of making it async, the question moved out of `ConfirmLeavingMode` into public
  `MainViewModel.ModeSwitchConfirmation(target)` (unchanged logic); the web asks it in the browser
  via `PlayerView.EnsureModeAsync` *before* invoking the switch, and `AlwaysConfirmDialog` then
  answers the view model's own ask. The desktop dialog is untouched. Revisit only if a second
  confirmation appears.
- **Web patterns worth keeping:** rows are addressed by a stable key (history: time + title; library
  and playlist: file path; stations: URL), never by list position — a new song or a save shifts
  positions between render and tap. A C# `bool` in an `aria-*` attribute renders valueless; write
  `"true"`/`"false"`. A running local web head locks `RadioPlayer.Core.dll` — stop it before building.
- **Desktop bug found and fixed on the way:** tapping a curated-playlist track after playing from
  the Songs tab played that *position of the whole library* (`PlayQueueItem` checked only that
  some queue existed). Now `MainViewModel.IsSameQueue`, with tests.
- **4 — MPRIS deferred (decided 2026-09-26).** On this headless Pi nothing would consume it: no
  desktop session, no KDE Connect, Bluetooth off with nothing paired, and a system service has no
  user session bus. Worth building only alongside Bluetooth audio (AVRCP buttons via `mpris-proxy`),
  which the ALSA-direct setup doesn't do today.
- **6 — done (branch `pi-web-look`).** The desktop's Crystal tokens in `wwwroot/app.css`; icons are
  the Theme.xaml geometries as inline SVG (`Components/Icon.razor`), never emoji; sparkle on AI
  actions only. Gotchas: static assets *linked* into `wwwroot` are registered but served empty in
  development — copy them (the fonts are copies of `Fonts/static`); put the page background on
  `html`, not `body`, or it paints over the fixed artwork layer only as far as the content goes;
  grid columns holding no-wrap ellipsis text need `minmax(0, 1fr)`. Headless Edge screenshots can't
  go below ~500 px wide (it lays out wider and crops), so check real phone width on a phone.
- **5 — done (branch `test-split`), as a split rather than just the four tests.** 12 of 57 test
  files depended on Windows. Now `tests/RadioPlayer.Core.Tests` (net10.0, 684 cases incl. theory
  rows) and `tests/RadioPlayer.Tests` (WPF head, 61). The four `DispatcherFrame` pumps became
  `TestLoop.Pump()` over `MessageLoop.RunPending()`; test threads get
  `MessageLoop.CreateForCurrentThread()` as their fallback (no SynchronizationContext — xUnit's
  must stay). `LyricsServiceTests` checks against `crystal-radio.csproj`'s `<Version>`
  (`Repo.ProductVersion()`) instead of the WPF `App`. Some tests start a real DJ harvest, whose
  thread opens BASS device 0 (no sound card needed), so the Core test project copies the BASS
  library for the building machine — Windows x64, Linux x64 (new: `native/linux-x64`) or Linux arm64.
  **Verified on the Pi:** all 682 (before the two RunPending tests were added) pass on Linux arm64 (SDK installed to a temp folder, removed after).
  Found on the way: a missing BASS library made `Bass.Init` *throw* on the harvest thread and take
  the whole process down; `HarvestThreadMain` now reports it as a failed session start.
- **DJ on the Pi 4 (2 GB), measured:** steady state harvesting 4 stations while playing is 5–8 % of
  one core; session start peaks ~70 % for ~30 s; finishing a song (QC + enrichment + embedding)
  peaks ~60–75 % briefly. Temperature ≤ 54.5 °C, never throttled, 0 errors. **Memory is a
  sawtooth, not a leak:** over 30 minutes and ~15 songs collected, RSS troughs stayed flat at
  580–640 MB and peaked at 926 MB while a song was processed (QC decodes the whole segment to PCM —
  a 6–8 min song is ~100–170 MB of samples — and GC returns it). Lowest `MemAvailable` seen: 816 MB
  of 1.8 GB. If headroom ever matters, `Environment=DOTNET_GCConserveMemory=5` in the unit is the
  first knob to try.

---

## Step 8 — Automatic Pi updates (designed 2026-09-26; built)

**Goal:** a push to `main` that passes every test reaches the Pi by itself, without interrupting
playback and without a bad build taking the radio down.

**Decided: build in GitHub Actions, not on the Pi.** A build on the 2 GB Pi 4 takes minutes and
lots of memory *while it plays* (Core build + tests measured at ~2¾ min; DJ sessions already peak at
~930 MB RSS), it could only ever run the Linux half of the tests, and a failed build would sit on the
listening device. The Pi only downloads finished, tested packages and reuses `deploy/pi/install.sh`.

**Pieces, in order (each its own session, each testable):**

1. **CI workflow** (`.github/workflows/`), on every push to `main`:
   - *Linux job* (ubuntu x64): `dotnet test tests/RadioPlayer.Core.Tests` (uses
     `native/linux-x64`), then `dotnet publish src/RadioPlayer.Web -r linux-arm64 --self-contained`
     (cross-compiles fine), package with `deploy/pi/*`, upload as a release/artifact.
   - *Windows job*: both test projects (`tests/RadioPlayer.Core.Tests`, `tests/RadioPlayer.Tests`).
   - Publish the Pi package **only if both jobs pass**. Judge by exit code.
   - The ONNX model (`MlAssets/all-MiniLM-L6-v2.onnx`, ~90 MB) is git-ignored: CI downloads it from
     the URL in README.md (cache it), **or** move it out of the app folder on the Pi so packages
     don't carry it — the latter needs a small code change (model path) and install.sh must stop
     deleting it. Decide when building this.
2. **Status endpoint** in the web head: read-only, e.g. `GET /api/status` →
   `{ playing, mode, djRunning, version }`, LAN-only like the rest. The updater needs to know whether
   something is playing, and there's no way to ask today.
3. **Pi updater**: a systemd timer (nightly, e.g. 04:00) + script that checks the latest package,
   skips if the status endpoint says something is playing, installs through `install.sh`, waits for
   the service to answer, and **rolls back to the previous version** if it doesn't (keep the previous
   `~/crystal-radio` as `~/crystal-radio.prev` instead of deleting it).

**Piece 1 — as implemented** (`.github/workflows/ci.yml`):
- Runs on **every push, any branch** (plus manual `workflow_dispatch`); only `main` publishes. A
  branch push is how a change gets tested in CI before it is merged.
- Jobs `linux` (Core tests, `linux-arm64` publish, package) and `windows` (both test projects) run in
  parallel; `release` needs both. `concurrency` cancels an older run on the same branch, so a stale
  build can't overwrite a newer package.
- **Model: CI downloads it** (`scripts/fetch-model.sh`, cached). Needed anyway, because the WPF
  head's build fails without the file. The script is pinned to upstream revision `1110a24` and checks
  the SHA-256 (`6fd5d72f…`), identical to the file the catalogs were embedded with. Moving the model
  out of the app folder isn't needed; the package is just larger.
- **Package:** `crystal-radio-pi.tar.gz` = `pi/` + `install.sh`, `setup.sh`, `crystal-radio.service`,
  the same layout `publish-pi.ps1` uploads, so `install.sh` serves both. `pi/build-info.json` holds
  `{ version, commit, built }` and ends up as `~/crystal-radio/build-info.json`; the updater compares
  its `commit` with the release's.
- **Published as** one rolling pre-release, tag **`pi-latest`**, deleted and re-created on every green
  push to `main`, with assets `crystal-radio-pi.tar.gz`, `crystal-radio-pi.tar.gz.sha256` and
  `build-info.json`. A release, not a workflow artifact, because release assets of a public repo
  download without any token and don't count against Actions storage. For a few seconds during the swap
  there is no `pi-latest`; the updater must treat a missing release as "nothing new".

**Piece 2 — as implemented** (`src/RadioPlayer.Web/Hosting/StatusEndpoint.cs`, mapped in
`Program.cs`): `GET /api/status` →
`{"playing":false,"mode":"radio","djRunning":false,"version":"1.10.0","commit":"43efb39…"}`.
- `playing` is `IsPlaying || IsBusy`: buffering and reconnecting count, since a listener is waiting.
  It covers all three engines (radio, library, DJ).
- `mode` is `radio` | `library` | `dj`. `djRunning` is true for a DJ session even while paused: it
  harvests in the background, so the updater should treat it as "in use" too.
- `version` and `commit` come from the assembly's informational version (`1.10.0+<sha>`), which the
  SDK stamps when building in a git checkout; `commit` is null otherwise. It matches
  `build-info.json`'s `commit` for a CI build.
- Read on the player thread through `PlayerHost.ReadAsync`, like every page. No auth, LAN-only.

**Piece 3 — as implemented** (`deploy/pi/update.sh`, `crystal-radio-update.{service,timer}`):
- The timer runs **every two hours** for now (`OnCalendar=*-*-* 00/2:15:00`, 5 min random delay,
  `Persistent=true`), at the user's request while the pipeline is new. The target is nightly.
- The running commit comes from `/api/status`, not a file, so a hand deploy (`publish-pi.ps1`, no
  `build-info.json`) is compared correctly too. A running build without the endpoint (404) is
  treated as busy: never update blind.
- Skips while `playing` or `djRunning`, checked before and again after the download. A service that
  doesn't answer at all counts as idle: nothing to interrupt, and a new build may be the fix.
- Download goes to `~/crystal-radio.download` (the Pi's `/tmp` is RAM). SHA-256 checked; the
  extracted `build-info.json` must name the expected commit (CI may replace the release mid-run).
- `install.sh` now keeps the replaced app as `~/crystal-radio.prev` (hand deploys too). After
  install the updater waits up to 60 s for `/api/status` to report the new commit; if not, it
  moves the new app to `~/crystal-radio.failed`, restores `.prev`, and writes the commit to
  `~/.local/state/crystal-radio-update/failed-commit` so it isn't retried. If `install.sh` stopped
  before swapping (e.g. couldn't stop the service), there's nothing to roll back and the commit
  isn't blamed.
- No new sudo rule: the updater only stops/starts the service, which the existing rule allows.
  `setup.sh` installs and enables the timer; re-run it once after the first deploy that ships it.
- JSON is read with `sed` (no `jq` on the Pi); `null` reads as empty.

**Open points, settled 2026-09-26:**
- The repo `hansee99/crystal-radio` is **public** (made so on 2026-09-26). The Pi downloads
  `pi-latest` anonymously, so **no token** is needed on the Pi. Standard runners are free for public
  repos, so Actions minutes aren't a constraint either.
- Every green push to `main` is published; the Pi applies it nightly. No tagged releases.
- `origin` now points at `https://github.com/hansee99/crystal-radio.git` (it was the pre-rename
  `radio-player` URL).

---

## Troubleshooting on the Pi

| Symptom | Cause / fix |
| --- | --- |
| `DllNotFoundException: bass` | `libbass.so` not next to the executable, or wrong arch. `ls`, then `file libbass.so` must say `ARM aarch64`. |
| Audio plays but is badly distorted | Device opened with more than 2 channels. While playing, `grep channels /proc/asound/card0/pcm0p/sub0/hw_params` must say `2`. Fix: `BassDevice.InitDefault` (Step 5), see "Step 0 finding". |
| `Bass.Init` → `Errors.Device` / `Driver` | ALSA can't open `default`. `aplay -l`; pick the output in `raspi-config`; check `libasound2` is installed. |
| AAC URL fails, MP3 works | `libbass_aac.so` missing or not loading. `ldd libbass_aac.so`. |
| ONNX load throws | Publish was not `linux-arm64` self-contained, so `libonnxruntime.so` for arm64 wasn't copied; or the `.onnx` is missing (git-ignored — see `MlAssets/README`). |
| Everything fails on a 32-bit OS | Must be 64-bit Raspberry Pi OS. `uname -m` → `aarch64`. |
| `Couldn't find a valid ICU package` | Install `libicu` (Step 0) or set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` in the unit file. |
| Search says "no API key" though the unit file sets one | `Environment=` lines take effect only after `systemctl daemon-reload` + restart. |
| Audio stutters | Try `Environment=DOTNET_TieredPGO=0` and a wired network; then look at BASS buffer settings in `RadioEngine` (none are set today). |
