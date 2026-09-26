using RadioPlayer.Threading;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// MessageLoop is the Dispatcher stand-in the DJ harvest thread runs on today and the web head's
/// player thread will run on — so it has to keep the promises a Dispatcher keeps: in-order, on
/// one thread, Invoke semantics for Send, timers that tick on the loop, awaits that come back to
/// it, and a shutdown that neither loses queued work nor hangs a caller.
/// </summary>
public class MessageLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs a loop on its own thread; Dispose shuts it down and joins.</summary>
    private sealed class LoopThread : IDisposable
    {
        private readonly Thread _thread;
        public MessageLoop Loop { get; }
        public int ThreadId { get; private set; }

        public LoopThread()
        {
            var ready = new TaskCompletionSource<MessageLoop>();
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                var loop = MessageLoop.InstallOnCurrentThread();
                ready.SetResult(loop);
                loop.Run();
            }) { IsBackground = true, Name = "MessageLoopTests" };
            _thread.Start();
            Loop = ready.Task.Wait(Timeout) ? ready.Task.Result : throw new TimeoutException("loop did not start");
        }

        public bool Join() => _thread.Join(Timeout);

        public void Dispose()
        {
            Loop.Shutdown();
            Join();
        }
    }

    [Fact]
    public async Task Post_RunsActionsInOrderOnTheLoopThread()
    {
        using var t = new LoopThread();
        var seen = new List<int>();
        var onLoop = true;
        var done = new TaskCompletionSource();

        for (var i = 0; i < 100; i++)
        {
            var n = i;
            t.Loop.Post(() =>
            {
                onLoop &= t.Loop.CheckAccess() && Environment.CurrentManagedThreadId == t.ThreadId;
                seen.Add(n);
            });
        }
        t.Loop.Post(done.SetResult);

        await done.Task.WaitAsync(Timeout);
        Assert.True(onLoop);
        Assert.Equal(Enumerable.Range(0, 100), seen);
    }

    [Fact]
    public void Send_FromAnotherThread_BlocksUntilTheActionHasRun()
    {
        using var t = new LoopThread();
        var ran = false;

        t.Loop.Send(() =>
        {
            Thread.Sleep(50); // the caller must still be waiting when this finishes
            ran = true;
        });

        Assert.True(ran);
    }

    [Fact]
    public async Task Send_OnTheLoopThread_RunsInline()
    {
        using var t = new LoopThread();
        var order = new List<string>();

        await t.Loop.InvokeAsync(() =>
        {
            t.Loop.Send(() => order.Add("inner")); // queued instead would deadlock or reorder
            order.Add("outer");
        }).WaitAsync(Timeout);

        Assert.Equal(["inner", "outer"], order);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesTheActionsException()
    {
        using var t = new LoopThread();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            t.Loop.InvokeAsync<int>(() => throw new InvalidOperationException("boom")).WaitAsync(Timeout));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Post_AThrowingActionDoesNotStopTheLoop()
    {
        using var t = new LoopThread();

        t.Loop.Post(() => throw new InvalidOperationException("a misbehaving callback"));
        var after = await t.Loop.InvokeAsync(() => 42).WaitAsync(Timeout);

        Assert.Equal(42, after);
    }

    [Fact]
    public async Task Timer_TicksOnTheLoopThread_AndStopsWhenStopped()
    {
        using var t = new LoopThread();
        var ticks = 0;
        var offLoop = 0;
        var three = new TaskCompletionSource();
        IDispatcherTimer timer = null!;

        await t.Loop.InvokeAsync(() =>
        {
            timer = t.Loop.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Tick += (_, _) =>
            {
                if (!t.Loop.CheckAccess()) Interlocked.Increment(ref offLoop);
                if (Interlocked.Increment(ref ticks) == 3) three.TrySetResult();
            };
            Assert.False(timer.IsEnabled);
            timer.Start();
            Assert.True(timer.IsEnabled);
        });

        await three.Task.WaitAsync(Timeout);
        await t.Loop.InvokeAsync(timer.Stop);
        var atStop = Volatile.Read(ref ticks);
        await Task.Delay(250);

        Assert.Equal(0, offLoop);
        Assert.False(timer.IsEnabled);
        Assert.Equal(atStop, Volatile.Read(ref ticks));
    }

    [Fact]
    public async Task Await_WithoutConfigureAwait_ResumesOnTheLoopThread()
    {
        using var t = new LoopThread();
        var resumedOnLoop = new TaskCompletionSource<bool>();

        t.Loop.Post(async () =>
        {
            await Task.Delay(20); // completes on a pool thread; the continuation must come back
            resumedOnLoop.SetResult(t.Loop.CheckAccess());
        });

        Assert.True(await resumedOnLoop.Task.WaitAsync(Timeout));
    }

    [Fact]
    public void Shutdown_RunsWorkQueuedAheadOfIt_ThenRunReturns()
    {
        var t = new LoopThread();
        var ran = 0;

        for (var i = 0; i < 10; i++)
            t.Loop.Post(() => { Thread.Sleep(5); ran++; });
        t.Loop.Shutdown();

        Assert.True(t.Join(), "Run() did not return after Shutdown");
        Assert.Equal(10, ran);
        Assert.True(t.Loop.HasShutdownStarted);
    }

    [Fact]
    public async Task AfterShutdown_PostIsDropped_AndSendDoesNotHang()
    {
        var t = new LoopThread();
        t.Loop.Shutdown();
        Assert.True(t.Join());
        var ran = false;

        t.Loop.Post(() => ran = true);
        // WaitAsync throws TimeoutException if Send hangs on the shut-down loop.
        await Task.Run(() => t.Loop.Send(() => ran = true)).WaitAsync(Timeout);

        Assert.False(ran);
    }
}
