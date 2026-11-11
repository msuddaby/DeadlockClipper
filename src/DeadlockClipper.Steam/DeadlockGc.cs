using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.Internal;

namespace DeadlockClipper.Steam;

public sealed class SteamLoginException(string message) : Exception(message);

public sealed class GcRequestException(string message, GcResult? result = null) : Exception(message)
{
    public GcResult? Result { get; } = result;
}

public sealed record MatchSalts(ulong MatchId, uint ClusterId, uint ReplaySalt, uint MetadataSalt, DateTimeOffset? ReplayValidThrough)
{
    private string Base => $"http://replay{ClusterId}.valve.net/{DeadlockGc.AppId}/{MatchId}";
    public string DemoUrl => $"{Base}_{ReplaySalt}.dem.bz2";
    public string MetadataUrl => $"{Base}_{MetadataSalt}.meta.bz2";
}

/// <summary>
/// A Steam client session that tells Steam it is running Deadlock and talks to the Deadlock Game Coordinator.
/// Use a secondary account: Steam allows one in-game session per account, so this would kick a player out of the game.
/// </summary>
public sealed class DeadlockGc : IAsyncDisposable
{
    public const uint AppId = 1422450;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    private readonly SteamClient _client = new();
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;
    private readonly SteamGameCoordinator _gc;
    private readonly SteamFriends _friends;
    private readonly CancellationTokenSource _pumpStop = new();
    private readonly Task _pump;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<IPacketGCMsg>> _pending = new();

    private TaskCompletionSource _connected = NewTcs();
    private TaskCompletionSource<SteamUser.LoggedOnCallback> _loggedOn = NewTcs<SteamUser.LoggedOnCallback>();
    private TaskCompletionSource _welcome = NewTcs();

    public bool IsReady => _welcome.Task.IsCompletedSuccessfully;
    public SteamID? SteamId => _client.SteamID;

    public DeadlockGc(ILogger log)
    {
        _log = log;
        _callbacks = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _gc = _client.GetHandler<SteamGameCoordinator>()!;
        _friends = _client.GetHandler<SteamFriends>()!;

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult());
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(cb => _loggedOn.TrySetResult(cb));
        _callbacks.Subscribe<SteamUser.LoggedOffCallback>(cb => _log.LogWarning("Logged off by Steam: {Result}", cb.Result));
        _callbacks.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage);
        _callbacks.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);

        _pump = Task.Run(() =>
        {
            while (!_pumpStop.IsCancellationRequested) _callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(500));
        });
    }

    /// <summary>
    /// Username/password login with Steam Guard (code or mobile confirmation via <paramref name="authenticator"/>).
    /// Returns a refresh token to save; later sessions log in with <see cref="ConnectAsync"/> and never need the password.
    /// </summary>
    public async Task<SteamToken> LoginWithPasswordAsync(string username, string password, IAuthenticator authenticator,
        string? guardData, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        var session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = true,
            GuardData = guardData,
            Authenticator = authenticator,
            DeviceFriendlyName = "DeadlockClipper",
        });
        var result = await session.PollingWaitForResultAsync(ct);
        return new SteamToken(result.AccountName, result.RefreshToken, result.NewGuardData ?? guardData);
    }

    /// <summary>Logs in with a saved token, starts "playing" Deadlock and waits for the Game Coordinator's welcome.</summary>
    public async Task ConnectAsync(SteamToken token, CancellationToken ct)
    {
        if (IsReady) return;
        await EnsureConnectedAsync(ct);

        _loggedOn = NewTcs<SteamUser.LoggedOnCallback>();
        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = token.AccountName,
            AccessToken = token.RefreshToken,
            ShouldRememberPassword = true,
        });
        var logon = await _loggedOn.Task.WaitAsync(RequestTimeout, ct);
        if (logon.Result != EResult.OK)
            throw new SteamLoginException(logon.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired
                ? $"Steam rejected the saved login ({logon.Result}); log in again."
                : $"Steam login failed: {logon.Result}.");
        _log.LogInformation("Logged in to Steam as {Account}", token.AccountName);

        var play = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        play.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(AppId) });
        _client.Send(play);

        // The GC ignores hellos until it has seen the session start playing; keep saying hello until it answers.
        _welcome = NewTcs();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!_welcome.Task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new GcRequestException("The Deadlock Game Coordinator didn't answer (is the account allowed to play Deadlock?).");
            _gc.Send(new ClientGCMsgProtobuf<CMsgClientHello>(GcMsg.ClientHello), AppId);
            await Task.WhenAny(_welcome.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            ct.ThrowIfCancellationRequested();
        }
        _log.LogInformation("Connected to the Deadlock Game Coordinator");
    }

    /// <summary>One page of a player's recent matches, newest first. Pass the returned cursor to continue.</summary>
    public async Task<CMsgClientToGCGetMatchHistoryResponse> GetMatchHistoryAsync(uint accountId, ulong cursor, CancellationToken ct)
    {
        var request = new ClientGCMsgProtobuf<CMsgClientToGCGetMatchHistory>(GcMsg.GetMatchHistory);
        request.Body.AccountId = accountId;
        request.Body.ContinueCursor = cursor;
        var response = await RequestAsync<CMsgClientToGCGetMatchHistory, CMsgClientToGCGetMatchHistoryResponse>(request, ct);
        if (response.Result != GcResult.Success)
            throw new GcRequestException(response.Result == GcResult.InvalidPermission
                ? "Valve only shares match history with Steam friends: send the clipper account a friend request from this player's account."
                : $"Match history request failed: {response.Result}.", response.Result);
        return response;
    }

    /// <summary>The salts that make up a match's replay and metadata download URLs.</summary>
    /// <param name="targetAccountId">A player in the match; the game client sends the player it's looking at.</param>
    public async Task<MatchSalts> GetMatchSaltsAsync(ulong matchId, uint targetAccountId, CancellationToken ct)
    {
        var request = new ClientGCMsgProtobuf<CMsgClientToGCGetMatchMetaData>(GcMsg.GetMatchMetaData);
        request.Body.MatchId = matchId;
        if (targetAccountId != 0) request.Body.TargetAccountId = targetAccountId;
        var r = await RequestAsync<CMsgClientToGCGetMatchMetaData, CMsgClientToGCGetMatchMetaDataResponse>(request, ct);
        if (r.Result != GcResult.Success)
            throw new GcRequestException(r.Result switch
            {
                GcResult.RateLimited => "Rate limited by the Game Coordinator; try again later.",
                GcResult.InvalidMatch => $"Match {matchId} doesn't exist.",
                GcResult.MatchInFlight => $"Match {matchId} is still being played.",
                _ => $"Match lookup failed: {r.Result}.",
            }, r.Result);
        return new MatchSalts(matchId, r.ClusterId, r.ReplaySalt, r.MetadataSalt,
            r.ReplayValidThrough > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.ReplayValidThrough) : null);
    }

    private async Task<TResponse> RequestAsync<TRequest, TResponse>(ClientGCMsgProtobuf<TRequest> request, CancellationToken ct)
        where TRequest : IExtensible, new()
        where TResponse : IExtensible, new()
    {
        if (!IsReady) throw new InvalidOperationException("Not connected to the Game Coordinator.");
        var jobId = _client.GetNextJobID();
        request.SourceJobID = jobId;
        var tcs = new TaskCompletionSource<IPacketGCMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[jobId.Value] = tcs;
        try
        {
            _gc.Send(request, AppId);
            var packet = await tcs.Task.WaitAsync(RequestTimeout, ct);
            return new ClientGCMsgProtobuf<TResponse>(packet).Body;
        }
        catch (TimeoutException)
        {
            throw new GcRequestException("The Game Coordinator didn't respond in time.");
        }
        finally
        {
            _pending.TryRemove(jobId.Value, out _);
        }
    }

    private void OnGcMessage(SteamGameCoordinator.MessageCallback cb)
    {
        if (cb.AppID != AppId) return;
        if (cb.EMsg == GcMsg.ClientWelcome)
        {
            _welcome.TrySetResult();
            return;
        }
        if (_pending.TryRemove(cb.Message.TargetJobID.Value, out var tcs)) tcs.TrySetResult(cb.Message);
    }

    /// <summary>
    /// The GC only shares match history with the account itself and its Steam friends, so the clipper account accepts
    /// friend requests: send one from your main account and it's accepted the next time this session is online.
    /// </summary>
    private void OnFriendsList(SteamFriends.FriendsListCallback cb)
    {
        foreach (var f in cb.FriendList.Where(f => f.Relationship == EFriendRelationship.RequestRecipient))
        {
            _log.LogInformation("Accepting friend request from {SteamId}", f.SteamID.ConvertToUInt64());
            _friends.AddFriend(f.SteamID);
        }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        _log.LogInformation("Disconnected from Steam{User}", cb.UserInitiated ? "" : " (unexpected)");
        var error = new SteamLoginException("Disconnected from Steam.");
        _connected.TrySetException(error);
        _loggedOn.TrySetException(error);
        _welcome.TrySetException(error);
        foreach (var p in _pending.Values) p.TrySetException(error);
        _connected = NewTcs();
        _welcome = NewTcs();
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected) return;
        _connected = NewTcs();
        _client.Connect();
        await _connected.Task.WaitAsync(RequestTimeout, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            _user.LogOff();
            _client.Disconnect();
        }
        _pumpStop.Cancel();
        await _pump;
        _pumpStop.Dispose();
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
