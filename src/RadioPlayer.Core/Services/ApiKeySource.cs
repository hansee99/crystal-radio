namespace RadioPlayer.Services;

/// <summary>
/// The Anthropic API key, held by reference rather than copied by value.
///
/// <para>Every LLM-backed service used to take a <c>string? apiKey</c> in its constructor and keep
/// it. The composition root reads the key once at startup, so pasting a key into the options
/// dialog changed nothing until the app was restarted — eight services were still holding the
/// value from launch. Passing one of these instead means they all read the same slot, and a change
/// reaches services that were built long before it.</para>
///
/// <para>Deliberately tiny and not an interface: there is nothing to substitute. Tests and every
/// existing call site keep passing a plain string thanks to the implicit conversion below.</para>
/// </summary>
public sealed class ApiKeySource
{
    // Written from the UI thread when the dialog saves; read from background threads (enrichment,
    // harvest QC, the agentic loop). Reference-type writes are already atomic — volatile is here
    // so a reader thread can't keep serving a cached copy after the key changes.
    private volatile string? _current;

    public ApiKeySource(string? key = null) => _current = Normalize(key);

    /// <summary>The key in effect right now, or null when there isn't one. Read it at the moment
    /// of use, never into a field.</summary>
    public string? Current
    {
        get => _current;
        set => _current = Normalize(value);
    }

    /// <summary>Whether an LLM call can be made at all. Services gate on this instead of
    /// re-testing the string, so "configured" means the same thing everywhere.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_current);

    /// <summary>
    /// Lets a bare string stand in for a source. Kept because the alternative is churning ~30 test
    /// call sites and every future one to say <c>new ApiKeySource("test-key")</c>, which reads no
    /// better and would make a service harder to construct in a test than it is today.
    /// </summary>
    public static implicit operator ApiKeySource(string? key) => new(key);

    /// <summary>Blank and whitespace both mean "no key" — the options dialog clears the key by
    /// saving an empty box, and an env var can be set to "".</summary>
    private static string? Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
