using System;

namespace JpEnChat.Translation;

/// <summary>
/// An error reported by OpenRouter: a non-2xx response, an <c>error</c> object in a 200 body or SSE chunk,
/// or <c>finish_reason: "error"</c>.
/// </summary>
public class OpenRouterException : Exception
{
    public OpenRouterException()
    {
    }

    public OpenRouterException(string message)
        : base(message)
    {
    }

    public OpenRouterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OpenRouterException(int? statusCode, string message, string? limitSource = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        LimitSource = limitSource;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status, or the numeric <c>error.code</c> of a mid-stream error. Null when unknown.</summary>
    public int? StatusCode { get; }

    /// <summary><c>error.metadata.limit_source</c> when OpenRouter says which limit was hit (key, account, ...).</summary>
    public string? LimitSource { get; }

    /// <summary>Parsed <c>Retry-After</c> header, if any.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>No OpenRouter API key is configured; thrown before any network I/O.</summary>
public sealed class MissingApiKeyException : OpenRouterException
{
    public MissingApiKeyException()
        : base(null, "No OpenRouter API key is set.")
    {
    }

    public MissingApiKeyException(string message)
        : base(null, message)
    {
    }

    public MissingApiKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
