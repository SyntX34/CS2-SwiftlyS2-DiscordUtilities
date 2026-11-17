namespace DiscordUtilities.Services;

public static class DiscordPlayerFormat
{
    public static string PlayerLink(string name, ulong steamId)
    {
        var safe = name.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("@", "＠").Replace("\n", " ").Replace("\r", " ");
        return steamId == 0 ? safe : $"[{safe}](<https://steamcommunity.com/profiles/{steamId}>)";
    }
}
