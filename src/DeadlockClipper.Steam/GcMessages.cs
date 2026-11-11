using ProtoBuf;

namespace DeadlockClipper.Steam;

// Hand-written protobuf-net contracts for the few Deadlock Game Coordinator messages we use. SteamKit2's GC message
// wrapper needs protobuf-net types; field numbers are copied from the game's own definitions in
// lib/demofile-net/DemoFile.Game.Deadlock/Protobufs (gcsdk_gcmessages.proto, citadel_gcmessages_client.proto).

/// <summary>Message ids (EGCBaseClientMsg, EGCCitadelClientMessages).</summary>
public static class GcMsg
{
    public const uint ClientWelcome = 4004;
    public const uint ClientHello = 4006;
    public const uint GetMatchHistory = 9112;
    public const uint GetMatchHistoryResponse = 9113;
    public const uint GetMatchMetaData = 9167;
    public const uint GetMatchMetaDataResponse = 9168;
}

public abstract class GcMessage : IExtensible
{
    private IExtension? _extension;
    IExtension IExtensible.GetExtensionObject(bool createIfMissing) => Extensible.GetExtensionObject(ref _extension, createIfMissing);
}

[ProtoContract]
public sealed class CMsgClientHello : GcMessage
{
    [ProtoMember(1)] public uint Version { get; set; }
    // ESourceEngine: 1 = Source 2.
    [ProtoMember(7, IsRequired = true)] public int Engine { get; set; } = 1;
}

[ProtoContract]
public sealed class CMsgClientWelcome : GcMessage
{
    [ProtoMember(1)] public uint Version { get; set; }
}

[ProtoContract]
public sealed class CMsgClientToGCGetMatchHistory : GcMessage
{
    [ProtoMember(1)] public uint AccountId { get; set; }
    [ProtoMember(2)] public ulong ContinueCursor { get; set; }
}

/// <summary>Shared by the history and metadata responses.</summary>
public enum GcResult
{
    InternalError = 0,
    Success = 1,
    InvalidPermission = 2,
    TemporarilyDisabled = 3,
    TooBusy = 4,
    RateLimited = 5,
    InvalidMatch = 6,
    MatchInFlight = 7,
}

[ProtoContract]
public sealed class CMsgClientToGCGetMatchHistoryResponse : GcMessage
{
    [ProtoContract]
    public sealed class Match : GcMessage
    {
        [ProtoMember(1)] public ulong MatchId { get; set; }
        [ProtoMember(2)] public uint HeroId { get; set; }
        [ProtoMember(3)] public uint MatchDurationS { get; set; }
        [ProtoMember(4)] public uint StartTime { get; set; }
        [ProtoMember(5)] public uint MatchResult { get; set; }
        [ProtoMember(6)] public int PlayerTeam { get; set; }
        [ProtoMember(7)] public uint PlayerKills { get; set; }
        [ProtoMember(8)] public uint PlayerDeaths { get; set; }
        [ProtoMember(9)] public uint PlayerAssists { get; set; }
        [ProtoMember(11)] public uint LastHits { get; set; }
        [ProtoMember(12)] public uint Denies { get; set; }
        [ProtoMember(13)] public uint HeroLevel { get; set; }
        [ProtoMember(14)] public uint NetWorth { get; set; }
    }

    // Kept as a number: protobuf-net throws on enum values it doesn't know.
    [ProtoMember(1)] public int ResultCode { get; set; }
    public GcResult Result => (GcResult)ResultCode;
    [ProtoMember(2)] public ulong ContinueCursor { get; set; }
    [ProtoMember(3)] public List<Match> Matches { get; set; } = [];
}

[ProtoContract]
public sealed class CMsgClientToGCGetMatchMetaData : GcMessage
{
    [ProtoMember(1)] public ulong MatchId { get; set; }
    [ProtoMember(3)] public uint MetadataSalt { get; set; }
    [ProtoMember(4)] public uint TargetAccountId { get; set; }
}

[ProtoContract]
public sealed class CMsgClientToGCGetMatchMetaDataResponse : GcMessage
{
    // Kept as a number: protobuf-net throws on enum values it doesn't know.
    [ProtoMember(1)] public int ResultCode { get; set; }
    public GcResult Result => (GcResult)ResultCode;
    [ProtoMember(2)] public uint ReplaySalt { get; set; }
    [ProtoMember(3)] public uint MetadataSalt { get; set; }
    [ProtoMember(4)] public uint ReplayValidThrough { get; set; }
    [ProtoMember(5)] public uint ClusterId { get; set; }
    [ProtoMember(6)] public uint ReplayProcessingThrough { get; set; }
}
