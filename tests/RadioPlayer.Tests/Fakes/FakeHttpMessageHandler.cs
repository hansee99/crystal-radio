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

    /// <summary>Bodies of the requests that were actually sent, in order.</summary>
    public List<string> Requests { get; } = new();

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
        Requests.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"FakeHttpMessageHandler ran out of scripted responses after {Requests.Count} request(s).");

        return _responses.Dequeue()(request);
    }
}
