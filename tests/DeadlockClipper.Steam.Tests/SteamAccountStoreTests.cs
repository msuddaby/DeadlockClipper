using System.Text.Json;
using DeadlockClipper.Steam;

namespace DeadlockClipper.Steam.Tests;

public sealed class SteamAccountStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"steam-accounts-{Guid.NewGuid():N}");
    private string Dir => Path.Combine(_root, "steam-accounts");

    [Fact]
    public void SavesListsAndDeletes_CaseInsensitively()
    {
        var store = new SteamAccountStore(Dir);
        store.Save(new SteamToken("BotOne", "t1", null));
        store.Save(new SteamToken("bot_two", "t2", "guard"));
        store.Save(new SteamToken("botone", "t1-new", null)); // same account, new token

        Assert.Equal(["botone", "bot_two"], store.List().Select(t => t.AccountName));
        Assert.Equal("t1-new", store.Get("BOTONE")!.RefreshToken);

        Assert.True(store.Delete("BotOne"));
        Assert.Equal(["bot_two"], store.List().Select(t => t.AccountName));
    }

    [Fact]
    public void TokenFiles_AreOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new SteamAccountStore(Dir);
        store.Save(new SteamToken("bot", "secret", null));
        var file = Directory.GetFiles(Dir).Single();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public void MigratesTheOldSingleTokenFile()
    {
        Directory.CreateDirectory(_root);
        var legacy = Path.Combine(_root, "steam-token.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new SteamToken("clipperbot", "tok", "g")));

        var accounts = new SteamAccountStore(Dir).List();

        Assert.Equal("tok", Assert.Single(accounts).RefreshToken);
        Assert.False(File.Exists(legacy));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
