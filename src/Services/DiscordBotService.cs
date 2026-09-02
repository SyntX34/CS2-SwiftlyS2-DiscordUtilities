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
    private readonly Func<bool> _isDebugEnabled;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private int _heartbeatIntervalMs = 41250;
    private int? _lastSequence;
    private bool _disposed;

    public DiscordBotService(ILogger logger, Action<string, string> onMessageReceived, Func<bool>? isDebugEnabled = null)
    {
        _logger = logger;
        _onMessageReceived = onMessageReceived;
        _isDebugEnabled = isDebugEnabled ?? (() => false);
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
                if (_isDebugEnabled())
                    _logger.LogWarning(ex, "[DiscordUtilities] Discord Gateway connection lost. Reconnecting in 5s...");
                await Task.Delay(5000, ct);
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
                if (_isDebugEnabled())
                    _logger.LogWarning("[DiscordUtilities] Discord Gateway Session Invalidated (Op 9). Token or Intents might be invalid.");
                break;

            case 0: // Dispatch
                var t = root.GetProperty("t").GetString();
                if (t == "READY")
                {
                    if (_isDebugEnabled())
                    {
                        var user = root.GetProperty("d").GetProperty("user");
                        var botName = user.GetProperty("username").GetString() ?? "Bot";
                        _logger.LogInformation("[DiscordUtilities] Discord Bot successfully logged in as: {BotName}", botName);
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
                            var authorName = author.TryGetProperty("global_name", out var gName) && gName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(gName.GetString())
                                ? gName.GetString()!
                                : author.GetProperty("username").GetString() ?? "User";

                            var content = d.GetProperty("content").GetString() ?? "";

                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                _onMessageReceived(authorName, content);
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
}
