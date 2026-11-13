using System.Net;

namespace Medical.Fhir.Http;

/// <summary>Retry policy for national-platform APIs (SATUSEHAT, KFA, BPJS).</summary>
public sealed record RetryOptions
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>
    /// Non-idempotent requests (POST/PATCH) are retried only when the server did not process them: 429 and 503,
    /// or a connection failure before a response. Set to true to also retry them on 500/502/504/timeouts.
    /// </summary>
    public bool RetryNonIdempotentOnServerError { get; init; }
}

/// <summary>
/// Exponential backoff with full jitter for transient failures: 408, 429, 500, 502, 503, 504, connection errors and
/// timeouts. Honours <c>Retry-After</c>. Request bodies are buffered so they can be resent.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly RetryOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="inner">Inner handler for standalone use; leave null inside an IHttpClientFactory pipeline.</param>
    public RetryHandler(RetryOptions? options = null, HttpMessageHandler? inner = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (inner is not null) InnerHandler = inner;
        _options = options ?? new RetryOptions();
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Number of retries performed (for diagnostics and tests).</summary>
    public int Retries { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var headers = request.Content?.Headers.ToList();
        bool idempotent = request.Method != HttpMethod.Post && request.Method != HttpMethod.Patch;
        for (int attempt = 0; ; attempt++)
        {
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                foreach (var h in headers!) request.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            HttpResponseMessage? response = null;
            bool retry;
            TimeSpan? retryAfter = null;
            try
            {
                response = await base.SendAsync(request, ct).ConfigureAwait(false);
                int code = (int)response.StatusCode;
                retry = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                        || ((idempotent || _options.RetryNonIdempotentOnServerError) && code is 408 or 500 or 502 or 504);
                retryAfter = response.Headers.RetryAfter?.Delta
                             ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries) { retry = true; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _options.MaxRetries && (idempotent || _options.RetryNonIdempotentOnServerError)) { retry = true; }

            if (!retry || attempt >= _options.MaxRetries) return response!;
            response?.Dispose();
            Retries++;
            double exp = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
            var wait = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * exp);
            await _delay(wait > _options.MaxDelay ? _options.MaxDelay : wait, ct).ConfigureAwait(false);
        }
    }
}
