using System.Net;
using System.Net.Http;

namespace RadioPlayer.Tests.Fakes;

/// <summary>
/// Serves scripted HTTP responses so the LLM-backed services can be exercised end to end without
/// a network. Deliberately drives the real service rather than testing a private parser: the
/// value is in what the service DOES with a malformed, hostile or surprising model reply, and
/// that includes the request loop, not just the parse.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly object _gate = new();

    /// <summary>Bodies of the requests that were actually sent, in order.</summary>
    public List<string> Requests { get; } = new();

    /// <summary>URLs of the requests that were actually sent, in order — for tests about which
    /// host was reached rather than which reply came back.</summary>
    public List<Uri> RequestUris { get; } = new();

    /// <summary>
    /// Answers anything the scripted queue doesn't cover, keyed on the request. Use for tests
    /// that care about the URL rather than the call order — e.g. "this mirror is down, that one
    /// is up" — where a fixed queue can't express the rule.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    /// <summary>
    /// Artificial delay before each response. Without it a fake handler completes inline, so
    /// "parallel" callers actually run one after another and no interleaving is ever exercised —
    /// which silently makes a concurrency test prove nothing.
    /// </summary>
    public TimeSpan Latency { get; set; }

    public int CallCount => Requests.Count;

    /// <summary>Queues a 200 response whose body is a Messages-API reply containing
    /// <paramref name="text"/> as its single text block.</summary>
    public FakeHttpMessageHandler RespondWithText(string text, string stopReason = "end_turn")
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(text);
        return RespondWithJson($$"""
            { "stop_reason": {{System.Text.Json.JsonSerializer.Serialize(stopReason)}},
              "content": [ { "type": "text", "text": {{escaped}} } ] }
            """);
    }

    /// <summary>Queues a 200 response with a verbatim JSON body (for tool_use turns and
    /// deliberately malformed replies).</summary>
    public FakeHttpMessageHandler RespondWithJson(string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
        return this;
    }

    public FakeHttpMessageHandler RespondWithStatus(HttpStatusCode status, string body = "{}")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });
        return this;
    }

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);

        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, cancellationToken);

        // StationSearchService fans out several queries at once, so this really is called
        // concurrently — the queue and the logs both need guarding.
        lock (_gate)
        {
            RequestUris.Add(request.RequestUri!);
            Requests.Add(body);

            if (_responses.Count > 0)
                return _responses.Dequeue()(request);
        }

        if (Fallback is not null)
            return Fallback(request);
        throw new InvalidOperationException(
            $"FakeHttpMessageHandler ran out of scripted responses after {Requests.Count} request(s).");
    }
}
