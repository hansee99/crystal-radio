using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>Returns a pre-programmed sequence of results, one per call — lets a test give
/// DjQueueService a different answer for the warm-start call vs. each low-watermark top-up.
/// The last programmed result repeats for any extra calls beyond what was queued.</summary>
public sealed class FakeSongCurator : ISongCurator
{
    private readonly Queue<Func<Task<IReadOnlyList<CuratedSong>>>> _responses = new();
    private IReadOnlyList<CuratedSong> _lastResult = Array.Empty<CuratedSong>();

    public bool IsAvailable { get; set; } = true;
    public int CallCount { get; private set; }

    /// <summary>The prompt of the most recent call — how a vibe change is observed, since the
    /// service keeps the prompt privately and only its effect on the next top-up is visible.</summary>
    public string? LastPrompt { get; private set; }

    /// <summary>Queues an immediately-available result for the next CurateAsync call.</summary>
    public void Enqueue(IReadOnlyList<CuratedSong> result) =>
        _responses.Enqueue(() => Task.FromResult(result));

    /// <summary>Queues a result gated on a TaskCompletionSource, so a test can control exactly
    /// when a CurateAsync call completes — needed to verify the "don't re-enter while a top-up
    /// is already in flight" guard.</summary>
    public TaskCompletionSource<IReadOnlyList<CuratedSong>> EnqueueGated()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<CuratedSong>>();
        _responses.Enqueue(() => tcs.Task);
        return tcs;
    }

    public Task<IReadOnlyList<CuratedSong>> CurateAsync(string prompt, int max = 20,
        IReadOnlyCollection<string>? excludeKeys = null, bool requireRelevance = false,
        CancellationToken ct = default)
    {
        LastPrompt = prompt;
        CallCount++;
        if (_responses.Count > 0)
        {
            var next = _responses.Dequeue();
            var task = next();
            if (task.IsCompletedSuccessfully)
                _lastResult = task.Result;
            return task;
        }
        return Task.FromResult(_lastResult);
    }
}
