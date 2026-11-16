using System.Text.Json;
namespace DiscordUtilities.Services;
public static class DiscordText
{
    private static readonly object Gate = new();
    private static Func<string, object[], string?>? _coreTranslator;

    // Use SwiftlyS2 Core locale selection; independent of any hardcoded language filename.
    public static void SetCoreTranslator(Func<string, object[], string?> translator)
    {
        lock (Gate) _coreTranslator = translator;
    }
    private static Dictionary<string,string>? _strings;
    public static string Get(string key, string fallback, params object[] args)
    {
        lock (Gate)
        {
            // SwiftlyS2 resolves hu/ro/etc using the server's configured language.
            // A missing key falls through to the bundled English translation.
            if (_coreTranslator != null)
            {
                try
                {
                    var translated = _coreTranslator(key, args);
                    if (!string.IsNullOrWhiteSpace(translated) &&
                        !string.Equals(translated, key, StringComparison.OrdinalIgnoreCase))
                        return translated;
                }
                catch { /* use English fallback */ }
            }
            if (_strings == null)
            {
                _strings = new Dictionary<string,string>();
                try
                {
                    // Plugin files are not necessarily rooted at the process base directory.
                    var pluginDirectory = Path.GetDirectoryName(typeof(DiscordText).Assembly.Location);
                    var file = Path.Combine(pluginDirectory ?? AppContext.BaseDirectory, "resources", "translations", "en.jsonc");
                    if (!File.Exists(file))
                        file = Path.Combine(AppContext.BaseDirectory, "resources", "translations", "en.jsonc");
                    if (File.Exists(file))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                        foreach (var item in doc.RootElement.EnumerateObject())
                            if (item.Value.ValueKind == JsonValueKind.String) _strings[item.Name] = item.Value.GetString() ?? "";
                    }
                }
                catch { /* fallback below */ }
            }
            var format = _strings.TryGetValue(key, out var value) ? value : fallback;
            try { return string.Format(format, args); } catch { return fallback; }
        }
    }
}
