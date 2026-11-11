using Microsoft.Extensions.Logging;

namespace DeadlockClipper.Steam;

public sealed class SteamOptions
{
    /// <summary>Folder of saved account logins. Empty: <see cref="SteamAccountStore.DefaultDirectory"/>.</summary>
    public string AccountsDir { get; set; } = "";

    /// <summary>
    /// Minimum gap between metadata (salt) requests per account. Valve's limit is per account and undocumented; the one
    /// published measurement is ~17 per 30 min, so 110 s keeps each account just under it.
    /// </summary>
    public double MetadataIntervalSeconds { get; set; } = 110;

    /// <summary>
    /// Disconnect an account after this long without requests, so it doesn't sit "in game" all day. Longer than the
    /// gap between an account's paced requests (several minutes with a few accounts), or every request reconnects.
    /// </summary>
    public double IdleDisconnectSeconds { get; set; } = 600;

    /// <summary>
    /// TEMPORARY quota measurement: retry a rate-limited account every <see cref="QuotaProbeRetryMinutes"/> instead of
    /// backing off exponentially, so the quota log pins down how long a block lasts.
    /// </summary>
    public bool QuotaProbe { get; set; }

    public double QuotaProbeRetryMinutes { get; set; } = 5;

    public string ResolvedAccountsDir => string.IsNullOrWhiteSpace(AccountsDir) ? SteamAccountStore.DefaultDirectory : AccountsDir;
}

public sealed record AccountStatus(
    string Account,
    bool Connected,
    string? LastError,
    // When this account may make its next metadata request.
    DateTimeOffset? NextRequestAt,
    // Set while Valve is refusing this account's metadata requests.
    DateTimeOffset? RateLimitedUntil,
    int RequestsMade);

/// <summary>
/// The clipper's Steam accounts, each with its own Game Coordinator session. Metadata requests are spread across
/// accounts, each paced to <see cref="SteamOptions.MetadataIntervalSeconds"/> and backed off on its own when Valve
/// rate-limits it. Match history goes to whichever account is the player's Steam friend.
/// </summary>
public sealed class GcPool(SteamOptions options, ILoggerFactory loggers, QuotaLog? quotaLog = null) : IAsyncDisposable
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(60);

    private readonly ILogger _log = loggers.CreateLogger<GcPool>();
    private readonly SteamAccountStore _store = new(options.ResolvedAccountsDir);
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    private TimeSpan Interval => TimeSpan.FromSeconds(options.MetadataIntervalSeconds);

    public SteamAccountStore Store => _store;

    public IReadOnlyList<AccountStatus> Status()
    {
        var now = DateTimeOffset.UtcNow;
        return Slots().Select(s => new AccountStatus(
            s.Name,
            s.Gc?.IsReady == true,
            s.LastError,
            s.NextRequestAt > now ? s.NextRequestAt : null,
            s.LimitedUntil > now ? s.LimitedUntil : null,
            s.Requests)).ToList();
    }

    /// <summary>When every account is rate-limited: the earliest time one is free again.</summary>
    public DateTimeOffset? AllLimitedUntil()
    {
        var now = DateTimeOffset.UtcNow;
        var slots = Slots();
        if (slots.Count == 0 || slots.Any(s => !(s.LimitedUntil > now))) return null;
        return slots.Min(s => s.LimitedUntil);
    }

    /// <summary>Tries each account until one is allowed to see the player's history (needs to be their Steam friend).</summary>
    public async Task<CMsgClientToGCGetMatchHistoryResponse> GetMatchHistoryAsync(uint accountId, ulong cursor, CancellationToken ct)
    {
        var slots = Slots().OrderBy(s => s.LastError != null).ToList();
        if (slots.Count == 0) throw NoAccounts();

        Exception? permission = null, other = null;
        foreach (var slot in slots)
        {
            try
            {
                var history = await RunAsync(slot, gc => gc.GetMatchHistoryAsync(accountId, cursor, ct), ct);
                Record(slot, QuotaRequest.History, null);
                return history;
            }
            catch (GcRequestException ex) when (ex.Result == GcResult.InvalidPermission)
            {
                Record(slot, QuotaRequest.History, ex);
                permission ??= ex;
            }
            catch (Exception ex) when (ex is SteamLoginException or GcRequestException or TimeoutException)
            {
                Record(slot, QuotaRequest.History, ex);
                other ??= ex;
            }
        }
        throw permission ?? other!;
    }

    /// <summary>
    /// Salts for a match from the account that is free soonest, waiting for its pacing slot first. Moves on to another
    /// account if one is rate-limited or broken.
    /// </summary>
    public async Task<MatchSalts> GetMatchSaltsAsync(ulong matchId, uint targetAccountId, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= Slots().Count; attempt++)
        {
            var slot = await TakeWhenFreeAsync(ct);
            try
            {
                var salts = await RunAsync(slot, gc => gc.GetMatchSaltsAsync(matchId, targetAccountId, ct), ct);
                Record(slot, QuotaRequest.Metadata, null);
                lock (_sync)
                {
                    slot.Backoff = FirstBackoff;
                    slot.LimitedUntil = null;
                    slot.Requests++;
                }
                return salts;
            }
            catch (GcRequestException ex) when (ex.Result is GcResult.RateLimited or GcResult.TooBusy)
            {
                Record(slot, QuotaRequest.Metadata, ex);
                lock (_sync)
                {
                    var rest = options.QuotaProbe ? TimeSpan.FromMinutes(options.QuotaProbeRetryMinutes) : slot.Backoff;
                    slot.LimitedUntil = DateTimeOffset.UtcNow + rest;
                    _log.LogInformation("Steam account {Account} rate-limited; resting it until {Until:t}", slot.Name, slot.LimitedUntil?.ToLocalTime());
                    slot.Backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, slot.Backoff.Ticks * 2));
                }
                last = ex;
            }
            catch (GcRequestException ex)
            {
                Record(slot, QuotaRequest.Metadata, ex); // invalid match etc.: the GC answered, so it counts
                throw;
            }
            catch (SteamLoginException ex)
            {
                Record(slot, QuotaRequest.Metadata, ex);
                lock (_sync) slot.LimitedUntil = DateTimeOffset.UtcNow + FirstBackoff; // broken login: try the others
                last = ex;
            }
        }
        throw last ?? NoAccounts();
    }

    /// <summary>Connects an account (e.g. right after adding it) to check it can reach Deadlock's Game Coordinator.</summary>
    public async Task VerifyAsync(string accountName, CancellationToken ct)
    {
        var slot = Slots().FirstOrDefault(s => string.Equals(s.Name, accountName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new SteamLoginException($"No saved login for {accountName}.");
        await RunAsync(slot, _ => Task.FromResult(true), ct);
    }

    public async Task RemoveAsync(string accountName)
    {
        _store.Delete(accountName);
        Slot? slot;
        lock (_sync)
        {
            if (!_slots.Remove(accountName, out slot)) return;
        }
        await slot.DisposeAsync();
    }

    private void Record(Slot slot, QuotaRequest request, Exception? error)
    {
        if (quotaLog == null) return;
        var outcome = error switch
        {
            null => QuotaOutcome.Ok,
            GcRequestException { Result: GcResult.RateLimited or GcResult.TooBusy } => QuotaOutcome.RateLimited,
            GcRequestException { Result: not null } => QuotaOutcome.Refused,
            _ => QuotaOutcome.Failed,
        };
        try { quotaLog.Record(slot.Name, request, outcome, error?.Message); }
        catch (IOException ex) { _log.LogDebug(ex, "Couldn't write the quota log"); }
    }

    /// <summary>
    /// Waits for an account whose pacing allows a request now and books it. Re-checks every few seconds rather than
    /// committing to one account up front, so an account added or freed meanwhile is used straight away. When every
    /// account is rate-limited it throws instead of waiting, so callers can show that and decide.
    /// </summary>
    private async Task<Slot> TakeWhenFreeAsync(CancellationToken ct)
    {
        while (true)
        {
            var slots = Slots();
            if (slots.Count == 0) throw NoAccounts();
            if (AllLimitedUntil() is { } until)
                throw new GcRequestException($"All Steam accounts are rate-limited by Valve until {until.ToLocalTime():h:mm tt}.", GcResult.RateLimited);

            TimeSpan wait;
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                DateTimeOffset FreeAt(Slot s) => new[] { now, s.NextRequestAt, s.LimitedUntil ?? now }.Max();
                var slot = slots.MinBy(FreeAt)!;
                var at = FreeAt(slot);
                if (at <= now)
                {
                    slot.NextRequestAt = now + Interval;
                    return slot;
                }
                wait = at - now;
            }
            await Task.Delay(wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task<T> RunAsync<T>(Slot slot, Func<DeadlockGc, Task<T>> request, CancellationToken ct)
    {
        await slot.Lock.WaitAsync(ct);
        try
        {
            slot.IdleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (slot.Gc is not { IsReady: true })
                    {
                        await slot.DropAsync();
                        var token = _store.Get(slot.Name) ?? throw new SteamLoginException($"The saved login for {slot.Name} is gone.");
                        slot.Gc = new DeadlockGc(loggers.CreateLogger<DeadlockGc>());
                        await slot.Gc.ConnectAsync(token, ct);
                    }
                    var result = await request(slot.Gc);
                    slot.LastError = null;
                    return result;
                }
                catch (SteamLoginException ex) when (attempt == 1 && ex.Message.StartsWith("Disconnected", StringComparison.Ordinal))
                {
                    // Steam sometimes drops a fresh session (e.g. right after another login on the account); retry once.
                    _log.LogInformation("Steam connection for {Account} dropped; reconnecting", slot.Name);
                    await slot.DropAsync();
                }
                catch (Exception ex) when (ex is SteamLoginException or GcRequestException or TimeoutException)
                {
                    // Request-level refusals (rate limit, permission) aren't the account's fault; everything else is.
                    if (ex is not GcRequestException { Result: not null })
                    {
                        slot.LastError = ex.Message;
                        await slot.DropAsync();
                    }
                    throw;
                }
            }
        }
        finally
        {
            slot.IdleTimer ??= new Timer(_ => _ = slot.DisconnectIfIdleAsync(_log));
            slot.IdleTimer.Change(TimeSpan.FromSeconds(options.IdleDisconnectSeconds), Timeout.InfiniteTimeSpan);
            slot.Lock.Release();
        }
    }

    /// <summary>The saved accounts, picking up ones added or removed since last time.</summary>
    private List<Slot> Slots()
    {
        var names = _store.List().Select(t => t.AccountName).ToList();
        List<Slot> gone;
        lock (_sync)
        {
            foreach (var n in names)
                if (!_slots.ContainsKey(n)) _slots[n] = new Slot(n) { Backoff = FirstBackoff };
            gone = _slots.Values.Where(s => !names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var s in gone) _slots.Remove(s.Name);
        }
        foreach (var s in gone) _ = s.DisposeAsync().AsTask();
        lock (_sync) return _slots.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SteamLoginException NoAccounts() =>
        new("No Steam accounts added. Add one under Matches → Clipper accounts (it needs Deadlock in its library).");

    public async ValueTask DisposeAsync()
    {
        List<Slot> all;
        lock (_sync)
        {
            all = _slots.Values.ToList();
            _slots.Clear();
        }
        foreach (var s in all) await s.DisposeAsync();
    }

    private sealed class Slot(string name) : IAsyncDisposable
    {
        public string Name { get; } = name;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DeadlockGc? Gc { get; set; }
        public Timer? IdleTimer { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset NextRequestAt { get; set; }
        public DateTimeOffset? LimitedUntil { get; set; }
        public TimeSpan Backoff { get; set; }
        public int Requests { get; set; }

        public async Task DisconnectIfIdleAsync(ILogger log)
        {
            if (!await Lock.WaitAsync(0)) return; // busy: the request in flight re-arms the timer
            try
            {
                if (Gc != null) log.LogInformation("Steam account {Account} idle; disconnecting", Name);
                await DropAsync();
            }
            finally
            {
                Lock.Release();
            }
        }

        public async Task DropAsync()
        {
            if (Gc == null) return;
            var gc = Gc;
            Gc = null;
            try { await gc.DisposeAsync(); } catch (Exception) { /* closing anyway */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (IdleTimer != null) await IdleTimer.DisposeAsync();
            await DropAsync();
        }
    }
}
