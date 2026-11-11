using System.Text.Json;

namespace DeadlockClipper.Steam;

/// <summary>A saved Steam login. The refresh token stands in for the password (valid for months); treat it like one.</summary>
public sealed record SteamToken(string AccountName, string RefreshToken, string? GuardData);

/// <summary>
/// Saved logins for the clipper's Steam accounts, one owner-only file per account in a folder. A login saved by an
/// older version as a single steam-token.json next to the folder is moved in on first use.
/// </summary>
public sealed class SteamAccountStore(string directory)
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "deadlock-clipper", "steam-accounts");

    public string Directory { get; } = directory;

    public IReadOnlyList<SteamToken> List()
    {
        MigrateLegacy();
        if (!System.IO.Directory.Exists(Directory)) return [];
        return System.IO.Directory.GetFiles(Directory, "*.json")
            .Select(Read)
            .OfType<SteamToken>()
            .OrderBy(t => t.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public SteamToken? Get(string accountName) => List().FirstOrDefault(t => Same(t.AccountName, accountName));

    public void Save(SteamToken token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(token.AccountName);
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(tmp, options))
        {
            JsonSerializer.Serialize(f, token);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public bool Delete(string accountName)
    {
        var path = PathFor(accountName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // Steam account names are case-insensitive; file names use the lower-cased name.
    private string PathFor(string accountName)
    {
        var safe = string.Concat(accountName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        return Path.Combine(Directory, safe + ".json");
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static SteamToken? Read(string path)
    {
        try { return JsonSerializer.Deserialize<SteamToken>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    private void MigrateLegacy()
    {
        var legacy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Directory))!, "steam-token.json");
        if (!File.Exists(legacy)) return;
        if (Read(legacy) is { } token) Save(token);
        File.Delete(legacy);
    }
}
