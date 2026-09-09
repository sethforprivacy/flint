using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Flint.Services;

public class SparkPluginUpdateChecker(
    SettingsRepository settingsRepository,
    PluginService pluginService,
    IHttpClientFactory httpClientFactory,
    ILogger<SparkPluginUpdateChecker> logger) : IPeriodicTask
{
    internal const string HttpClientName = "SparkPluginUpdate";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task Do(CancellationToken cancellationToken)
    {
        var settings = await settingsRepository
            .GetSettingAsync<SparkServerSettings>()
            .ConfigureAwait(false) ?? new SparkServerSettings();

        if (string.IsNullOrWhiteSpace(settings.UpdateWebhookUrl))
            return;

        if (!pluginService.Installed.TryGetValue(Constants.PluginIdentifier, out var installedVersion))
        {
            logger.LogDebug("Flint: not found in installed plugins; skipping update check");
            return;
        }

        Version? latestRemote = null;
        try
        {
            var remotePlugins = await pluginService
                .GetRemotePlugins(Constants.PluginIdentifier, cancellationToken)
                .ConfigureAwait(false);

            foreach (var plugin in remotePlugins)
            {
                if (string.Equals(plugin.Identifier, Constants.PluginIdentifier, StringComparison.OrdinalIgnoreCase)
                    && (latestRemote is null || plugin.Version > latestRemote))
                {
                    latestRemote = plugin.Version;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Flint: could not fetch remote plugin versions; skipping update check");
            return;
        }

        if (latestRemote is null || latestRemote <= installedVersion)
            return;

        var availableVersionStr = latestRemote.ToString();
        if (availableVersionStr == settings.LastNotifiedUpdateVersion)
            return;

        if (!Uri.TryCreate(settings.UpdateWebhookUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            logger.LogWarning(
                "Flint: update webhook URL '{Url}' is not a valid http/https URL; notification skipped",
                settings.UpdateWebhookUrl);
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            @event = "plugin.update-available",
            pluginIdentifier = Constants.PluginIdentifier,
            installedVersion = installedVersion.ToString(),
            availableVersion = availableVersionStr
        }, SerializerOptions);

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Flint: update webhook returned {Status}", (int)response.StatusCode);
                return;
            }

            logger.LogInformation("Flint: notified update webhook of version {Version}", availableVersionStr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Flint: update webhook delivery failed");
            return;
        }

        settings.LastNotifiedUpdateVersion = availableVersionStr;
        await settingsRepository.UpdateSetting(settings).ConfigureAwait(false);
    }
}
