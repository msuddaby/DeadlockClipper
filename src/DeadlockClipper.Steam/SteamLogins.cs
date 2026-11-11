using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;

namespace DeadlockClipper.Steam;

public enum LoginState
{
    Starting,
    // Waiting for a Steam Guard code (CodeKind says which).
    NeedsCode,
    // Waiting for the user to approve the sign-in in the Steam mobile app.
    ConfirmOnPhone,
    // Logged in; checking the account can reach Deadlock's Game Coordinator.
    Verifying,
    Done,
    Failed,
}

/// <param name="CodeKind">"authenticator" (Steam Guard mobile code) or "email".</param>
public sealed record LoginStatus(
    Guid Id,
    string Username,
    LoginState State,
    string? CodeKind = null,
    string? EmailHint = null,
    bool PreviousCodeWrong = false,
    string? Error = null);

/// <summary>
/// Adds clipper accounts from the web UI: a password login driven step by step (Steam Guard code or mobile
/// approval), after which only the refresh token is saved. Passwords are never stored.
/// </summary>
public sealed class SteamLogins(GcPool pool, ILoggerFactory loggers)
{
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, Flow> _flows = new();

    public LoginStatus Start(string username, string password)
    {
        foreach (var stale in _flows.Values.Where(f => f.Finished < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10)).ToList())
            _flows.TryRemove(stale.Id, out _);

        var flow = new Flow(Guid.NewGuid(), username.Trim());
        _flows[flow.Id] = flow;
        _ = RunAsync(flow, password);
        return flow.Status;
    }

    public LoginStatus? Get(Guid id) => _flows.TryGetValue(id, out var f) ? f.Status : null;

    public bool SubmitCode(Guid id, string code) => _flows.TryGetValue(id, out var f) && f.SubmitCode(code.Trim());

    private async Task RunAsync(Flow flow, string password)
    {
        var log = loggers.CreateLogger<SteamLogins>();
        using var cts = new CancellationTokenSource(FlowTimeout);
        try
        {
            SteamToken token;
            await using (var gc = new DeadlockGc(loggers.CreateLogger<DeadlockGc>()))
            {
                token = await gc.LoginWithPasswordAsync(flow.Username, password, flow,
                    pool.Store.Get(flow.Username)?.GuardData, cts.Token);
            }
            pool.Store.Save(token);
            log.LogInformation("Added Steam account {Account}", token.AccountName);

            flow.Set(LoginState.Verifying);
            try
            {
                await pool.VerifyAsync(token.AccountName, cts.Token);
            }
            catch (Exception ex) when (ex is GcRequestException or SteamLoginException or TimeoutException)
            {
                flow.Fail($"Logged in, but Deadlock's servers didn't answer: does {token.AccountName} have Deadlock in its library? ({ex.Message})");
                return;
            }
            flow.Set(LoginState.Done);
        }
        catch (AuthenticationException ex)
        {
            flow.Fail(ex.Result switch
            {
                EResult.InvalidPassword => "Wrong username or password.",
                EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle => "Steam is throttling logins for this account; try again later.",
                EResult.Expired or EResult.Timeout => "The sign-in wasn't approved in time.",
                _ => $"Steam login failed: {ex.Result}.",
            });
        }
        catch (OperationCanceledException)
        {
            flow.Fail("The sign-in wasn't completed within 5 minutes.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Adding Steam account {Account} failed", flow.Username);
            flow.Fail(ex.Message);
        }
    }

    private sealed class Flow(Guid id, string username) : IAuthenticator
    {
        private readonly object _sync = new();
        private TaskCompletionSource<string>? _code;
        private LoginStatus _status = new(id, username, LoginState.Starting);

        public Guid Id { get; } = id;
        public string Username { get; } = username;
        public DateTimeOffset? Finished { get; private set; }

        public LoginStatus Status
        {
            get { lock (_sync) return _status; }
        }

        public void Set(LoginState state)
        {
            lock (_sync) _status = _status with { State = state, CodeKind = null, PreviousCodeWrong = false };
            if (state == LoginState.Done) Finished = DateTimeOffset.UtcNow;
        }

        public void Fail(string error)
        {
            lock (_sync) _status = _status with { State = LoginState.Failed, Error = error };
            _code?.TrySetCanceled();
            Finished = DateTimeOffset.UtcNow;
        }

        public bool SubmitCode(string code)
        {
            lock (_sync)
            {
                if (_status.State != LoginState.NeedsCode || _code == null || code.Length == 0) return false;
                _status = _status with { State = LoginState.Starting };
                return _code.TrySetResult(code);
            }
        }

        private Task<string> AskForCode(string kind, string? email, bool previousWrong)
        {
            lock (_sync)
            {
                _code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _status = _status with { State = LoginState.NeedsCode, CodeKind = kind, EmailHint = email, PreviousCodeWrong = previousWrong };
                return _code.Task;
            }
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => AskForCode("authenticator", null, previousCodeWasIncorrect);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => AskForCode("email", email, previousCodeWasIncorrect);

        /// <summary>Prefer a tap in the Steam mobile app over typing a code.</summary>
        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            lock (_sync) _status = _status with { State = LoginState.ConfirmOnPhone };
            return Task.FromResult(true);
        }
    }
}
