using System.Net;
using System.Text;

namespace ProductionWatch;

public sealed record Delivery(bool Attempted, bool Delivered, int Attempts, string Detail);

/// <summary>
/// Sends alerts to a webhook as JSON: a "text" summary (accepted by Slack and Teams incoming webhooks) plus the alert
/// list. Retries network errors, timeouts, 429 and 5xx with exponential backoff; other 4xx answers are permanent. The
/// URL is a secret from an environment variable; plain http is refused except to localhost.
/// </summary>
public sealed class Notifier(HttpClient http, AlertConfig config, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static Uri? WebhookFromEnvironment(AlertConfig config, IDictionary<string, string?>? environment = null)
    {
        var value = environment is null
            ? Environment.GetEnvironmentVariable(config.WebhookUrlEnvironmentVariable)
            : environment.TryGetValue(config.WebhookUrlEnvironmentVariable, out var v) ? v : null;
        // Azure Pipelines passes an undefined variable on as the literal text "$(NAME)": that means "not set".
        if (string.IsNullOrWhiteSpace(value) || (value.StartsWith("$(", StringComparison.Ordinal) && value.EndsWith(')')))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException($"{config.WebhookUrlEnvironmentVariable} must be an https URL (http only for localhost).");
        }

        return uri;
    }

    public async Task<Delivery> SendAsync(Uri webhook, IReadOnlyList<Alert> alerts, string summary, Window window, CancellationToken cancellationToken)
    {
        // Serialised up front so the request carries a Content-Length: some webhook receivers refuse chunked bodies.
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            text = summary,
            source = "PilotGate Production Watch",
            window = new { start = window.Start, end = window.End },
            alerts = alerts.Select(a => new { severity = a.Severity.ToString().ToLowerInvariant(), rule = a.Rule, scope = a.Scope, message = a.Message }),
        });

        var detail = new StringBuilder();
        var backoff = TimeSpan.FromMilliseconds(config.InitialBackoffMilliseconds);
        for (var attempt = 1; attempt <= config.MaxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(webhook, content, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return new Delivery(true, true, attempt, $"delivered ({(int)response.StatusCode})");
                }

                var status = (int)response.StatusCode;
                detail.Append($"attempt {attempt}: HTTP {status}; ");
                if (status != 429 && status < 500)
                {
                    return new Delivery(true, false, attempt, detail + "permanent error, not retried");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter?.Delta is { } delta)
                {
                    retryAfter = delta < TimeSpan.FromSeconds(30) ? delta : TimeSpan.FromSeconds(30);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                detail.Append($"attempt {attempt}: {(ex is TaskCanceledException ? "timeout" : "network error")}; ");
            }

            if (attempt < config.MaxAttempts)
            {
                await _delay(retryAfter ?? backoff, cancellationToken);
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 8000));
            }
        }

        return new Delivery(true, false, config.MaxAttempts, detail + "gave up");
    }
}
