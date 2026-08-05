using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>Returns a pre-programmed sequence of results, one per call — lets a test give
/// DjQueueService a different answer for the warm-start call vs. each low-watermark top-up.
/// The last programmed result repeats for any extra calls beyond what was queued.
///
/// <para>The song-based Enqueue overloads are kept so existing tests read unchanged: the curator now
/// returns a <see cref="CurationResult"/>, but most tests only care about the songs in it.</para></summary>
public sealed class FakeSongCurator : ISongCurator
{
    private readonly Queue<Func<Task<CurationResult>>> _responses = new();
    private CurationResult _lastResult = CurationResult.Empty(CurationOutcome.LibraryEmpty);

    public bool IsAvailable { get; set; } = true;
    public int CallCount { get; private set; }

    /// <summary>The prompt of the most recent call — how a vibe change is observed, since the
    /// service keeps the prompt privately and only its effect on the next top-up is visible.</summary>
    public string? LastPrompt { get; private set; }

    /// <summary>Queues an immediately-available result for the next CurateAsync call. An empty list
    /// reports NothingRelevant, which is the ordinary reason a real seed comes back empty.</summary>
    public void Enqueue(IReadOnlyList<CuratedSong> result) =>
        Enqueue(result, result.Count > 0 ? CurationOutcome.Ok : CurationOutcome.NothingRelevant);

    /// <summary>Queues a result with an explicit outcome — for the tests about WHY a seed was
    /// empty, which is the whole reason the curator reports one.</summary>
    public void Enqueue(IReadOnlyList<CuratedSong> result, CurationOutcome outcome, double bestScore = 0.4)
    {
        var value = new CurationResult(result, outcome, bestScore);
        _responses.Enqueue(() => Task.FromResult(value));
    }

    /// <summary>
    /// Queues a result gated on a completion source, so a test can control exactly when a
    /// CurateAsync call completes — needed to verify the "don't re-enter while a top-up is already
    /// in flight" guard.
    ///
    /// <para>Returns a handle rather than the <see cref="TaskCompletionSource{T}"/> itself, and that
    /// is load-bearing: the gated task must BE the completion source's task, so SetResult completes
    /// it inline. An <c>async</c> wrapper that awaited the source and re-wrapped the value resumed
    /// on the thread pool instead, and the in-flight test began racing its own dispatcher pump.</para>
    /// </summary>
    public GatedCuration EnqueueGated()
    {
        var tcs = new TaskCompletionSource<CurationResult>();
        _responses.Enqueue(() => tcs.Task);
        return new GatedCuration(tcs);
    }

    /// <summary>Lets a test complete a gated call with songs, without knowing about CurationResult.</summary>
    public sealed class GatedCuration(TaskCompletionSource<CurationResult> source)
    {
        public void SetResult(IReadOnlyList<CuratedSong> songs) =>
            source.SetResult(new CurationResult(songs,
                songs.Count > 0 ? CurationOutcome.Ok : CurationOutcome.NothingRelevant, 0.4));
    }

    public Task<CurationResult> CurateAsync(string prompt, int max = 20,
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
