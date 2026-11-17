using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DiscordUtilities.Config;

namespace DiscordUtilities.Services;

public sealed class DiscordBotService : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action<string, string> _onMessageReceived;
    private readonly Action<string, string, string, string>? _onRawMessageReceived;
    private readonly Func<bool> _isDebugEnabled;
    private readonly Action<string, string, string, string, string, string>? _onButton;
    private static readonly HttpClient _httpClient = new();
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private int _heartbeatIntervalMs = 41250;
    private int? _lastSequence;
    private bool _disposed;

    public DiscordBotService(
        ILogger logger,
        Action<string, string> onMessageReceived,
        Func<bool>? isDebugEnabled = null,
        Action<string, string, string, string>? onRawMessageReceived = null,
        Action<string, string, string, string, string, string>? onButton = null)
    {
        _logger = logger;
        _onMessageReceived = onMessageReceived;
        _isDebugEnabled = isDebugEnabled ?? (() => false);
        _onRawMessageReceived = onRawMessageReceived;
        _onButton = onButton;
    }

    public void Start(string botToken, string channelId)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(channelId))
            return;

        Stop();

        _cts = new CancellationTokenSource();
        _ = ConnectAndListenAsync(botToken, channelId, _cts.Token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _webSocket?.Dispose();
            _webSocket = null;
        }
        catch { }
    }

    private async Task ConnectAndListenAsync(string botToken, string channelId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                _webSocket = new ClientWebSocket();
                await _webSocket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), ct);
                if (_isDebugEnabled())
                    _logger.LogInformation("[DiscordUtilities] Connected to Discord Gateway");

                var receiveBuffer = new byte[8192];

                while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (_isDebugEnabled())
                                _logger.LogInformation("[DiscordUtilities] Discord Gateway received close frame: {Status} ({Desc})", result.CloseStatus, result.CloseStatusDescription);
                            try
                            {
                                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                            }
                            catch { }
                            break;
                        }

                        if (result.Count > 0)
                        {
                            ms.Write(receiveBuffer, 0, result.Count);
                        }
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (ms.Length == 0)
                        continue;

                    ms.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(ms, Encoding.UTF8);
                    var rawJson = await reader.ReadToEndAsync(ct);

                    if (string.IsNullOrWhiteSpace(rawJson))
                        continue;

                    await HandleGatewayMessageAsync(rawJson, botToken, channelId, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DiscordUtilities] Discord Gateway connection lost. Reconnecting in 5s...");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public static async Task<(bool IsValid, string BotUsername, string ErrorReason)> ValidateBotTokenAsync(string botToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            return (false, "", "Bot token is empty");

        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
            request.Headers.Add("Authorization", $"Bot {botToken.Trim()}");
            request.Headers.Add("User-Agent", "DiscordUtilities-SwiftlyS2/1.0");

            var res = await client.SendAsync(request);
            if (res.IsSuccessStatusCode)
            {
                var content = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? "Bot" : "Bot";
                return (true, username, "");
            }

            return (false, "", $"Discord API returned {(int)res.StatusCode} ({res.ReasonPhrase}) - check Bot Token");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    private async Task HandleGatewayMessageAsync(string rawJson, string botToken, string channelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var op = root.GetProperty("op").GetInt32();

        if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number)
            _lastSequence = s.GetInt32();

        switch (op)
        {
            case 10: // Hello
                var heartbeatInterval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                _heartbeatIntervalMs = heartbeatInterval;
                _ = HeartbeatLoopAsync(ct);
                await IdentifyAsync(botToken, ct);
                break;

            case 9: // Invalid Session
                _logger.LogWarning("[DiscordUtilities] Discord Gateway invalid session. Verify Message Content Intent in the Discord Developer Portal and restart the plugin.");
                break;

            case 0: // Dispatch
                var t = root.GetProperty("t").GetString();
                if (t == "READY")
                {
                    _logger.LogInformation("[DiscordUtilities] Discord Gateway READY; listening for messages in channel {Channel}", channelId);
                    if (_isDebugEnabled())
                    {
                        var user = root.GetProperty("d").GetProperty("user");
                        var botName = user.GetProperty("username").GetString() ?? "Bot";
                        _logger.LogInformation("[DiscordUtilities] Discord Bot successfully logged in as: {BotName}", botName);
                    }
                }
                else if (t == "INTERACTION_CREATE" && _onButton != null)
                {
                    var d = root.GetProperty("d");
                    if (d.TryGetProperty("data", out var data) && data.TryGetProperty("custom_id", out var customId))
                    {
                        var member = d.TryGetProperty("member", out var m) ? m : default;
                        var user = member.ValueKind != JsonValueKind.Undefined && member.TryGetProperty("user", out var u)
                            ? u : d.GetProperty("user");
                        var userId = user.GetProperty("id").GetString() ?? "";
                        var userName = user.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String
                            ? gn.GetString() ?? "" : user.GetProperty("username").GetString() ?? "";
                        _onButton(customId.GetString() ?? "", userId, userName,
                            d.GetProperty("id").GetString() ?? "", d.GetProperty("token").GetString() ?? "",
                            d.TryGetProperty("type", out var interactionType) && interactionType.GetInt32() == 5
                                ? ExtractModalCode(data)
                                : d.TryGetProperty("message", out var msg) && msg.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "");
                    }
                }
                else if (t == "MESSAGE_CREATE")
                {
                    var d = root.GetProperty("d");
                    var msgChannelId = d.GetProperty("channel_id").GetString();

                    if (_isDebugEnabled())
                    {
                        var author = d.TryGetProperty("author", out var auth) ? auth : default;
                        var isBot = author.ValueKind != JsonValueKind.Undefined && author.TryGetProperty("bot", out var b) && b.GetBoolean();
                        var name = author.ValueKind != JsonValueKind.Undefined && author.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                        var text = d.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                        _logger.LogInformation("[DiscordUtilities] [Gateway Event] MESSAGE_CREATE in channel {EventChannel} (Configured: {ConfigChannel}) from {User} (IsBot: {IsBot}): {Text}",
                            msgChannelId, channelId, name, isBot, text);
                    }

                    if (string.Equals(msgChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                    {
                        var author = d.GetProperty("author");
                        var isBot = author.TryGetProperty("bot", out var botProp) && botProp.GetBoolean();

                        if (!isBot)
                        {
                            var authorId = author.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                            var authorName = author.TryGetProperty("global_name", out var gName) && gName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(gName.GetString())
                                ? gName.GetString()!
                                : author.GetProperty("username").GetString() ?? "User";

                            var content = d.GetProperty("content").GetString() ?? "";

                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                _logger.LogInformation("[DiscordUtilities] Relaying Discord message from {Author} to CS2", authorName);
                                _onMessageReceived(authorName, content);
                                _onRawMessageReceived?.Invoke(msgChannelId ?? "", authorId, authorName, content);
                            }
                        }
                    }
                }
                break;
        }
    }

    private async Task IdentifyAsync(string botToken, CancellationToken ct)
    {
        var identifyPayload = new
        {
            op = 2,
            d = new
            {
                token = botToken,
                intents = 513 | 32768 | 4096, // GUILDS (1) | GUILD_MESSAGES (512) | MESSAGE_CONTENT (32768) | DIRECT_MESSAGES (4096)
                properties = new
                {
                    os = "linux",
                    browser = "DiscordUtilities",
                    device = "DiscordUtilities"
                }
            }
        };

        var json = JsonSerializer.Serialize(identifyPayload);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(_heartbeatIntervalMs, ct);
                var hb = new { op = 1, d = _lastSequence };
                var json = JsonSerializer.Serialize(hb);
                var bytes = Encoding.UTF8.GetBytes(json);
                if (_webSocket?.State == WebSocketState.Open)
                    await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch { break; }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    private static string ExtractModalCode(JsonElement data)
    {
        if (!data.TryGetProperty("components", out var rows)) return "";
        foreach (var row in rows.EnumerateArray())
            if (row.TryGetProperty("components", out var fields))
                foreach (var field in fields.EnumerateArray())
                    if (field.TryGetProperty("value", out var value)) return value.GetString() ?? "";
        return "";
    }

    public static async Task OpenVerificationModalAsync(string interactionId, string interactionToken,
        string title, string label, ILogger logger)
    {
        try
        {
            var payload = new { type = 9, data = new
            {
                custom_id = "discordutilities:verify_submit", title,
                components = new[] { new { type = 1, components = new[] { new
                {
                    type = 4, custom_id = "verification_code", style = 1,
                    label, min_length = 6, max_length = 12, required = true
                } } } }
            } };
            using var response = await _httpClient.PostAsync(
                $"https://discord.com/api/v10/interactions/{interactionId}/{interactionToken}/callback",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode) logger.LogWarning("[DiscordUtilities] Verification modal failed: {Status}", response.StatusCode);
        }
        catch (Exception ex) { logger.LogError(ex, "[DiscordUtilities] Verification modal failed"); }
    }

    public static async Task AcknowledgeButtonAsync(string interactionId, string interactionToken, string? ephemeralText, ILogger logger)
    {
        try
        {
            var payload = ephemeralText == null ? new { type = 6, data = (object?)null }
                : new { type = 4, data = (object?)new { content = ephemeralText, flags = 64 } };
            using var res = await _httpClient.PostAsync($"https://discord.com/api/v10/interactions/{interactionId}/{interactionToken}/callback",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) logger.LogWarning("[DiscordUtilities] Button ACK failed: {Status}", res.StatusCode);
        }
        catch (Exception ex) { logger.LogError(ex, "[DiscordUtilities] Button ACK failed"); }
    }

    // Discord does not allow ordinary webhooks to own non-link interaction buttons.
    // Resolve the channel from the configured webhook URL, then send using the bot token.
    // Reuse a bot-owned verification panel after an upgrade even if the old release did not persist its ID.
    public static async Task<string?> FindExistingPanelAsync(string token, string channelId, string customId, ILogger logger)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/channels/{channelId}/messages?limit=100");
            req.Headers.Add("Authorization", $"Bot {token}");
            using var response = await _httpClient.SendAsync(req);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var message in doc.RootElement.EnumerateArray())
            {
                if (!message.TryGetProperty("components", out var rows)) continue;
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("components", out var buttons)) continue;
                    foreach (var button in buttons.EnumerateArray())
                    {
                        if (!button.TryGetProperty("custom_id", out var id)) continue;
                        var value = id.GetString();
                        if (value == customId || value == "discordutilities:verify_help")
                            return message.GetProperty("id").GetString();
                    }
                }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "[DiscordUtilities] Failed to discover existing verification panel"); }
        return null;
    }

    // Read the actual Discord message so report buttons remain usable after a server/plugin restart.
    public static async Task<DiscordEmbed?> GetInteractiveReportEmbedAsync(string token, string channelId,
        string messageId, string expectedCustomId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(channelId) ||
            string.IsNullOrWhiteSpace(messageId)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}");
            req.Headers.Add("Authorization", $"Bot {token}");
            using var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("[DiscordUtilities] Could not load report message from Discord ({Status})", res.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            // Only handle messages containing the expected report button.
            if (!root.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return null;
            var found = false;
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("components", out var buttons) || buttons.ValueKind != JsonValueKind.Array) continue;
                foreach (var button in buttons.EnumerateArray())
                {
                    if (!button.TryGetProperty("custom_id", out var id) || id.GetString() != expectedCustomId) continue;
                    if (button.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                        return null;
                    found = true;
                }
            }
            if (!found || !root.TryGetProperty("embeds", out var embeds) || embeds.GetArrayLength() == 0)
                return null;
            var embed = JsonSerializer.Deserialize<DiscordEmbed>(embeds[0].GetRawText());
            // A handled report is never reopened, even after a restart.
            if (embed?.Color == 0x57F287) return null;
            return embed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[DiscordUtilities] Failed to read existing Discord report");
            return null;
        }
    }

    public static async Task<string?> UpsertInteractiveEmbedAsync(string token, string channelId,
        DiscordEmbed embed, string customId, string buttonLabel, string? messageId = null,
        string? content = null, bool disableButton = false)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(channelId)) return null;
        try
        {
            var payload = new
            {
                content,
                allowed_mentions = new { parse = new[] { "roles", "users", "everyone" } },
                embeds = new[] { embed },
                components = new[] { new { type = 1, components = new[] { new
                {
                    type = 2, style = 1, custom_id = customId, label = buttonLabel, disabled = disableButton
                } } } }
            };
            using var req = new HttpRequestMessage(messageId == null ? HttpMethod.Post : HttpMethod.Patch,
                $"https://discord.com/api/v10/channels/{channelId}/messages" + (messageId == null ? "" : "/" + messageId));
            req.Headers.Add("Authorization", $"Bot {token}");
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await _httpClient.SendAsync(req);
            // A manually deleted panel cannot be PATCHed. Recreate it only for 404;
            // do not create duplicates on timeouts, permission errors or rate limits.
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound && messageId != null &&
                !customId.StartsWith("discordutilities:handle:", StringComparison.Ordinal) &&
                !customId.StartsWith("discordutilities:bughandle:", StringComparison.Ordinal))
                return await UpsertInteractiveEmbedAsync(token, channelId, embed, customId, buttonLabel);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch { return null; }
    }

    public static async Task SendChannelMessageAsync(string botToken, string channelId, string content, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(content))
            return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelId}/messages");
            req.Headers.Add("Authorization", $"Bot {botToken}");
            var payload = JsonSerializer.Serialize(new { content });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                logger?.LogWarning("[DiscordUtilities] Failed to send Discord message ({Status})", res.StatusCode);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[DiscordUtilities] Error sending Discord message");
        }
    }

    public static async Task<bool> HasGuildMemberRoleAsync(string botToken, string guildId, string userId, string roleId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(guildId) ||
            string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(roleId)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/members/{userId}");
            req.Headers.Add("Authorization", $"Bot {botToken}");
            using var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                logger?.LogWarning("[DiscordUtilities] Could not verify Discord member role ({Status})", res.StatusCode);
                return false;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array &&
                roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == roleId);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[DiscordUtilities] Discord member role check failed");
            return false;
        }
    }

    public static async Task<bool> AddGuildMemberRoleAsync(string botToken, string guildId, string userId, string roleId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(guildId) ||
            string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(roleId))
            return false;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"https://discord.com/api/v10/guilds/{guildId}/members/{userId}/roles/{roleId}");
            req.Headers.Add("Authorization", $"Bot {botToken}");
            using var res = await _httpClient.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                logger?.LogWarning("[DiscordUtilities] Failed to assign role {RoleId} to {UserId} ({Status})", roleId, userId, res.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[DiscordUtilities] Error assigning Discord role");
            return false;
        }
    }
    public static async Task<bool> RemoveGuildMemberRoleAsync(string botToken, string guildId, string userId, string roleId, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(guildId) ||
            string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(roleId)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete,
                $"https://discord.com/api/v10/guilds/{guildId}/members/{userId}/roles/{roleId}");
            req.Headers.Add("Authorization", $"Bot {botToken}");
            using var res = await _httpClient.SendAsync(req);
            if (res.IsSuccessStatusCode) return true;
            logger?.LogWarning("[DiscordUtilities] Failed to remove role {RoleId} from {UserId} ({Status})", roleId, userId, res.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[DiscordUtilities] Error removing Discord role");
            return false;
        }
    }

}
