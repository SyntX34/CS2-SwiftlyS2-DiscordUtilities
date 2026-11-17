using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DiscordUtilities.Services;

public sealed class WebhookService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public WebhookService(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DiscordUtilities-SwiftlyS2/1.0");
    }

    public async Task<bool> ValidateWebhookAsync(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return false;

        try
        {
            var response = await _httpClient.GetAsync(webhookUrl);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to validate webhook URL: {Url}", webhookUrl);
            return false;
        }
    }

    public async Task<string?> ResolveChannelIdAsync(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return null;
        try
        {
            using var response = await _httpClient.GetAsync(webhookUrl.Split('?')[0]);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("channel_id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[DiscordUtilities] Could not resolve webhook channel"); return null; }
    }

    public bool IsOnCooldown(string key, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0) return false;

        var now = DateTime.UtcNow;
        if (_cooldowns.TryGetValue(key, out var lastSent))
        {
            if ((now - lastSent).TotalSeconds < cooldownSeconds)
                return true;
        }

        _cooldowns[key] = now;
        return false;
    }

    public async Task SendEmbedAsync(string webhookUrl, DiscordEmbed embed, string? username = null, string? avatarUrl = null, string? content = null, List<DiscordComponentActionRow>? components = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        var payload = new WebhookPayload
        {
            Content = content,
            Username = username,
            AvatarUrl = avatarUrl,
            Embeds = [embed],
            Components = components
        };

        await SendPayloadAsync(webhookUrl, payload);
    }

    public async Task SendMessageAsync(string webhookUrl, string content, string? username = null, string? avatarUrl = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        var payload = new WebhookPayload
        {
            Content = content,
            Username = username,
            AvatarUrl = avatarUrl
        };

        await SendPayloadAsync(webhookUrl, payload);
    }

    // Webhook URLs remain the only destination configuration; wait=true returns a message ID for edits.
    public async Task<string?> UpsertEmbedAsync(string webhookUrl, DiscordEmbed embed, string? messageId,
        string? username = null, string? content = null, List<DiscordComponentActionRow>? components = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return null;
        var payload = new WebhookPayload { Embeds = [embed], Username = username, Content = content, Components = components };
        try
        {
            var url = messageId == null
                ? webhookUrl + (webhookUrl.Contains('?') ? "&wait=true" : "?wait=true")
                : webhookUrl.Split('?')[0].TrimEnd('/') + "/messages/" + Uri.EscapeDataString(messageId) + "?wait=true";
            using var request = new HttpRequestMessage(messageId == null ? HttpMethod.Post : HttpMethod.Patch, url);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && messageId != null)
            {
                // The saved status message was deleted. Create a fresh one and persist its new ID.
                _logger.LogInformation("[DiscordUtilities] Saved map status message {MessageId} no longer exists; creating a new message", messageId);
                return await UpsertEmbedAsync(webhookUrl, embed, null, username, content, components);
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DiscordUtilities] Webhook upsert failed ({Status})", response.StatusCode);
                return null;
            }
            // Discord webhook PATCH may succeed with HTTP 204 No Content.
            // A successful edit must keep the saved ID even if no JSON body is returned.
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return messageId;

            var responseBody = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(responseBody))
                return messageId;

            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : messageId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Webhook upsert failed");
            return null;
        }
    }

    private async Task SendPayloadAsync(string webhookUrl, WebhookPayload payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(webhookUrl, httpContent);

            if ((int)response.StatusCode == 429)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("[DiscordUtilities] Rate limited by Discord: {Body}", body);

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("retry_after", out var retryAfter))
                {
                    var waitMs = (int)(retryAfter.GetDouble() * 1000) + 100;
                    await Task.Delay(waitMs);

                    using var retryContent = new StringContent(json, Encoding.UTF8, "application/json");
                    await _httpClient.PostAsync(webhookUrl, retryContent);
                }
            }
            else if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError("[DiscordUtilities] Webhook failed ({StatusCode}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to send webhook");
        }
    }

    public static int ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var color))
            return color;
        return 0x5865F2;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

#region Webhook Models

public sealed class WebhookPayload
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("embeds")]
    public List<DiscordEmbed>? Embeds { get; set; }

    [JsonPropertyName("components")]
    public List<DiscordComponentActionRow>? Components { get; set; }
}

public sealed class DiscordComponentActionRow
{
    [JsonPropertyName("type")]
    public int Type { get; set; } = 1; // 1 = ActionRow

    [JsonPropertyName("components")]
    public List<DiscordComponentButton> Components { get; set; } = [];
}

public sealed class DiscordComponentButton
{
    [JsonPropertyName("type")]
    public int Type { get; set; } = 2; // 2 = Button

    [JsonPropertyName("style")]
    public int Style { get; set; } = 5; // 5 = Link Button

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("custom_id")]
    public string? CustomId { get; set; }

    [JsonPropertyName("emoji")]
    public DiscordEmoji? Emoji { get; set; }
}

public sealed class DiscordEmoji
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class DiscordEmbed
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("color")]
    public int? Color { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("footer")]
    public EmbedFooter? Footer { get; set; }

    [JsonPropertyName("thumbnail")]
    public EmbedImage? Thumbnail { get; set; }

    [JsonPropertyName("image")]
    public EmbedImage? Image { get; set; }

    [JsonPropertyName("author")]
    public EmbedAuthor? Author { get; set; }

    [JsonPropertyName("fields")]
    public List<EmbedField>? Fields { get; set; }

    public DiscordEmbed AddField(string name, string value, bool inline = false)
    {
        Fields ??= [];
        Fields.Add(new EmbedField { Name = name, Value = value, Inline = inline });
        return this;
    }

    public DiscordEmbed WithTimestamp()
    {
        Timestamp = DateTime.UtcNow.ToString("o");
        return this;
    }
}

public sealed class EmbedFooter
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}

public sealed class EmbedImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class EmbedAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}

public sealed class EmbedField
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("inline")]
    public bool? Inline { get; set; }
}

#endregion
