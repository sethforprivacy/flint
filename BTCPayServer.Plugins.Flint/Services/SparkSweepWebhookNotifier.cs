using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Flint.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// Delivers a webhook POST after each successful sweep.
/// </summary>
/// <remarks>
/// Transient failures (network errors and 5xx responses) are retried with exponential backoff:
/// up to <see cref="MaxAttempts"/> total attempts spaced 2 s, 4 s, 8 s apart.
/// Client errors (4xx) are not retried - they are permanent and will not resolve on their own.
/// The sweep record in the database is the authoritative source of truth; the webhook is a
/// convenience notification only, so a delivery failure never blocks or rolls back the sweep.
///
/// </remarks>
public class SparkSweepWebhookNotifier
{
    public const string HttpClientName = "SparkSweepWebhook";
    public const int MaxAttempts = 4; // 1 initial + 3 retries

    internal static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SparkSweepWebhookNotifier> _logger;
    private readonly TimeSpan[] _retryDelays;

    public SparkSweepWebhookNotifier(
        IHttpClientFactory httpClientFactory,
        ILogger<SparkSweepWebhookNotifier> logger,
        TimeSpan[]? retryDelays = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    public async Task NotifyAsync(
        string webhookUrl,
        string storeId,
        SweepRecord record,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseUrl(webhookUrl, storeId, out var uri))
            return;

        var json = JsonSerializer.Serialize(new
        {
            @event = "sweep.swept",
            storeId,
            idempotencyKey = record.IdempotencyKey,
            txId = record.TxId,
            amountSats = record.AmountSats,
            feeSats = record.FeeSats,
            destination = record.DestinationAddress,
            destinationMode = record.DestinationMode.ToString(),
            trigger = record.Trigger.ToString(),
            completedAt = record.CompletedAt
        }, SerializerOptions);

        await PostWithRetryAsync(uri!, webhookUrl, storeId, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyFailureAsync(
        string webhookUrl,
        string storeId,
        SweepTrigger trigger,
        string reason,
        SweepRecord? record,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseUrl(webhookUrl, storeId, out var uri))
            return;

        var json = JsonSerializer.Serialize(new
        {
            @event = "sweep.failed",
            storeId,
            trigger = trigger.ToString(),
            reason,
            idempotencyKey = record?.IdempotencyKey,
            amountSats = record?.AmountSats,
            destination = record?.DestinationAddress,
            destinationMode = record?.DestinationMode.ToString()
        }, SerializerOptions);

        await PostWithRetryAsync(uri!, webhookUrl, storeId, json, cancellationToken).ConfigureAwait(false);
    }

    private bool TryParseUrl(string webhookUrl, string storeId, out Uri? uri)
    {
        if (Uri.TryCreate(webhookUrl, UriKind.Absolute, out uri)
            && uri.Scheme is "http" or "https")
            return true;

        _logger.LogWarning(
            "Store {StoreId}: sweep webhook URL '{Url}' is not a valid http/https URL; notification skipped",
            storeId, webhookUrl);
        uri = null;
        return false;
    }

    private async Task PostWithRetryAsync(
        Uri uri,
        string webhookUrl,
        string storeId,
        string json,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient(HttpClientName);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return;

                var status = (int)response.StatusCode;

                // 4xx errors are permanent: the endpoint rejected the request and a retry will not help.
                if (status is >= 400 and < 500)
                {
                    _logger.LogWarning(
                        "Store {StoreId}: sweep webhook returned {StatusCode} (client error); not retrying",
                        storeId, status);
                    return;
                }

                _logger.LogWarning(
                    "Store {StoreId}: sweep webhook attempt {Attempt}/{Max} returned {StatusCode}",
                    storeId, attempt, MaxAttempts, status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Store {StoreId}: sweep webhook delivery attempt {Attempt}/{Max} to '{Url}' failed",
                    storeId, attempt, MaxAttempts, webhookUrl);
            }

            if (attempt < MaxAttempts)
                await Task.Delay(_retryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Store {StoreId}: sweep webhook delivery to '{Url}' failed after {Max} attempts; giving up",
            storeId, webhookUrl, MaxAttempts);
    }
}
