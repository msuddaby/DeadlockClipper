using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Steam;
using Google.Protobuf;
using ProtoBuf;

namespace DeadlockClipper.Steam.Tests;

public class MetadataAnalysisTests
{
    private const uint Me = 165461963, Enemy1 = 100, Enemy2 = 200, Enemy3 = 300;
    private static ulong Id(uint account) => MetadataAnalysis.ToSteamId64(account);

    private static CMsgMatchMetaDataContents.Types.Players Player(uint account, uint slot, ECitadelLobbyTeam team, uint hero,
        params (uint Time, uint KillerSlot)[] deaths)
    {
        var p = new CMsgMatchMetaDataContents.Types.Players { AccountId = account, PlayerSlot = slot, Team = team, HeroId = hero };
        foreach (var (t, k) in deaths) p.DeathDetails.Add(new CMsgMatchMetaDataContents.Types.Deaths { GameTimeS = t, KillerPlayerSlot = k });
        return p;
    }

    private static CMsgMatchMetaDataContents Match()
    {
        const ECitadelLobbyTeam A = ECitadelLobbyTeam.KEcitadelLobbyTeamTeam0, B = ECitadelLobbyTeam.KEcitadelLobbyTeamTeam1;
        var info = new CMsgMatchMetaDataContents.Types.MatchInfo { MatchId = 135555232, DurationS = 1900 };
        info.Players.Add(Player(Me, 7, B, 64, (600, 1)));
        // A triple kill by me at 25:42-25:56, plus a lone kill at 4:45 and a death to a trooper (slot 0).
        info.Players.Add(Player(Enemy1, 1, A, 87, (285, 7), (1542, 7), (900, 0)));
        info.Players.Add(Player(Enemy2, 2, A, 25, (1551, 7)));
        info.Players.Add(Player(Enemy3, 3, A, 3, (1556, 7)));
        return new CMsgMatchMetaDataContents { MatchInfo = info };
    }

    [Fact]
    public void ToKills_MapsSlotsToPlayersAndEstimatesTicks()
    {
        var (players, kills) = MetadataAnalysis.ToKills(Match());

        Assert.Equal(4, players.Count);
        Assert.Equal(Team.Sapphire, players.Single(p => p.SteamId == 76561198125727691).Team);
        Assert.Equal(6, kills.Count);
        Assert.Equal(kills.OrderBy(k => k.Tick), kills);

        var first = kills[0];
        Assert.Equal(285 * 64, first.Tick);
        Assert.Equal(76561198125727691UL, first.AttackerSteamId);
        Assert.Equal(64, first.AttackerHeroId);
        Assert.Equal(Id(Enemy1), first.VictimSteamId);

        var trooper = kills.Single(k => k.GameTimeSeconds == 900);
        Assert.Null(trooper.AttackerSteamId);
    }

    [Fact]
    public void Score_FindsThePlayersMultikills()
    {
        var potential = MetadataAnalysis.Score(Match(), Id(Me));

        Assert.Equal(4, potential.Kills);
        var h = Assert.Single(potential.Highlights);
        Assert.Equal(HighlightType.TripleKill, h.Type);
        Assert.Equal(1542, h.StartTimeSeconds);
        Assert.Equal(h.Score, potential.Score);

        Assert.Empty(MetadataAnalysis.Score(Match(), Id(Enemy1)).Highlights);
    }

    [Fact]
    public void Score_CountsTeamfightsThePlayerGotKillsIn()
    {
        // 8 kills 10 s apart at 20:00: Me gets 3 (one per enemy, victims cycle), the enemy team the rest.
        const ECitadelLobbyTeam A = ECitadelLobbyTeam.KEcitadelLobbyTeamTeam0, B = ECitadelLobbyTeam.KEcitadelLobbyTeamTeam1;
        var info = new CMsgMatchMetaDataContents.Types.MatchInfo { MatchId = 1, DurationS = 2400 };
        var me = Player(Me, 7, B, 64);
        var e1 = Player(Enemy1, 1, A, 87);
        var e2 = Player(Enemy2, 2, A, 25);
        var mate = Player(42, 8, B, 3);
        void Death(CMsgMatchMetaDataContents.Types.Players victim, uint t, uint killerSlot) =>
            victim.DeathDetails.Add(new CMsgMatchMetaDataContents.Types.Deaths { GameTimeS = t, KillerPlayerSlot = killerSlot });
        Death(e1, 1200, 7); Death(mate, 1210, 1); Death(e2, 1220, 7); Death(me, 1230, 2);
        Death(mate, 1240, 2); Death(e1, 1250, 7); Death(mate, 1260, 1); Death(me, 1270, 1);
        info.Players.AddRange([me, e1, e2, mate]);

        var potential = MetadataAnalysis.Score(new CMsgMatchMetaDataContents { MatchInfo = info }, Id(Me));

        var fight = Assert.Single(potential.Fights);
        Assert.Equal(3, fight.PlayerKills);
        Assert.Equal(8, fight.Fight.Kills.Count);
        Assert.Equal(13.5, fight.Score); // 1.5 x 3^2: about a triple kill
        Assert.Equal(Math.Round(potential.Highlights.Sum(h => h.Score) + fight.Score, 2), potential.Score);

        // A player with no kills in the fight gets nothing from it.
        Assert.Empty(MetadataAnalysis.Score(new CMsgMatchMetaDataContents { MatchInfo = info }, Id(42)).Fights);
    }

    [Fact]
    public void ParseMetadata_UnwrapsTheFileFormat()
    {
        var contents = Match();
        var file = new CMsgMatchMetaData { MatchId = 135555232, MatchDetails = contents.ToByteString() };

        var parsed = ReplayDownloader.ParseMetadata(new MemoryStream(file.ToByteArray()));

        Assert.Equal(contents, parsed);
    }

    [Theory]
    [InlineData("zstd")]
    [InlineData("bzip2")]
    public async Task Decompress_DetectsTheFormat(string format)
    {
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        var compressed = new MemoryStream();
        if (format == "zstd")
        {
            await using var z = new ZstdSharp.CompressionStream(compressed, leaveOpen: true);
            await z.WriteAsync(data);
        }
        else
        {
            await using var b = new ICSharpCode.SharpZipLib.BZip2.BZip2OutputStream(compressed) { IsStreamOwner = false };
            await b.WriteAsync(data);
        }
        compressed.Position = 0;

        await using var decompressed = await ReplayDownloader.DecompressAsync(compressed, default);
        var output = new MemoryStream();
        await decompressed.CopyToAsync(output);

        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void GcMessages_UseTheGamesFieldNumbers()
    {
        // match_id = 1 (varint), metadata_salt = 3: field 1 tag 0x08.
        var request = new CMsgClientToGCGetMatchMetaData { MatchId = 150 };
        var bytes = new MemoryStream();
        Serializer.Serialize(bytes, request);
        Assert.Equal(new byte[] { 0x08, 0x96, 0x01 }, bytes.ToArray());

        // target_account_id = 4: tag 0x20.
        bytes.SetLength(0);
        Serializer.Serialize(bytes, new CMsgClientToGCGetMatchMetaData { MatchId = 1, TargetAccountId = 5 });
        Assert.Equal(new byte[] { 0x08, 0x01, 0x20, 0x05 }, bytes.ToArray());

        // result = 1, replay_salt = 2, cluster_id = 5.
        var response = Serializer.Deserialize<CMsgClientToGCGetMatchMetaDataResponse>(
            new MemoryStream([0x08, 0x01, 0x10, 0x2A, 0x28, 0x81, 0x01]));
        Assert.Equal(GcResult.Success, response.Result);
        Assert.Equal(42u, response.ReplaySalt);
        Assert.Equal(129u, response.ClusterId);
    }
}
