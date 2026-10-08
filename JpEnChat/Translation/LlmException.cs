using System;

namespace JpEnChat.Translation;

/// <summary>
/// An error reported by an LLM service (<see cref="OpenRouterException"/>, <see cref="AnthropicException"/>) or a
/// malformed answer. <see cref="TranslationPipeline.DescribeError"/> turns it into the short text shown in the log.
/// </summary>
public class LlmException : Exception
{
    public LlmException()
    {
    }

    public LlmException(string message)
        : base(message)
    {
    }

    public LlmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LlmException(int? statusCode, string message, string? limitSource = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        LimitSource = limitSource;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP status, or the numeric code of a mid-stream error. Null when unknown.</summary>
    public int? StatusCode { get; }

    /// <summary>Which limit was hit (key, account, ...), when the service says so.</summary>
    public string? LimitSource { get; }

    /// <summary>Parsed <c>Retry-After</c> header, if any.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// An error reported by OpenRouter: a non-2xx response, an <c>error</c> object in a 200 body or SSE chunk,
/// or <c>finish_reason: "error"</c>.
/// </summary>
public class OpenRouterException : LlmException
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
        : base(statusCode, message, limitSource, retryAfter)
    {
    }
}

/// <summary>
/// An error reported by the Claude API: a non-2xx response (<c>{"type":"error","error":{"type":...,"message":...}}</c>)
/// or an <c>error</c> event in the stream.
/// </summary>
public sealed class AnthropicException : LlmException
{
    public AnthropicException()
    {
    }

    public AnthropicException(string message)
        : base(message)
    {
    }

    public AnthropicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AnthropicException(int? statusCode, string message, string? errorType = null, TimeSpan? retryAfter = null)
        : base(statusCode, message, null, retryAfter)
    {
        ErrorType = errorType;
    }

    /// <summary>The API's <c>error.type</c>, e.g. <c>invalid_request_error</c> or <c>overloaded_error</c>.</summary>
    public string? ErrorType { get; }

    /// <summary>The API's <c>error.message</c> without the "HTTP nnn:" prefix; null when there was none.</summary>
    public string? ApiMessage { get; init; }
}

/// <summary>The model declined to answer (<c>stop_reason: "refusal"</c>). Partial output must be discarded.</summary>
public sealed class LlmRefusalException : LlmException
{
    public LlmRefusalException()
        : base(null, "The model declined to translate this (refusal).")
    {
    }

    public LlmRefusalException(string message)
        : base(null, message)
    {
    }

    public LlmRefusalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No API key is configured for the selected provider; thrown before any network I/O.</summary>
public sealed class MissingApiKeyException : LlmException
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
