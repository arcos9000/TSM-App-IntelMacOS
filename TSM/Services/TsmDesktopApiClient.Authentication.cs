using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSM.Services;

public sealed partial class TsmDesktopApiClient
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Session? _session;

    public bool IsAuthenticated => Volatile.Read(ref _session) is not null;
    public bool IsPremium => Volatile.Read(ref _session)?.IsPremium == true;
    public IReadOnlyList<string> ReleaseChannels => Volatile.Read(ref _session)?.Channels ?? ["Release"];
    public string? RefreshToken => Volatile.Read(ref _session)?.RefreshToken;
    internal string? AccountId => Volatile.Read(ref _session)?.Subject;
    public event Action<string>? RefreshTokenChanged;

    public async Task<ApiLoginResult> SignInAsync(Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var form = await TsmBrowserSignIn.AuthorizeAsync(openBrowser, cancellationToken);
            return await CompleteSignInAsync(form, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AuthenticationFailure(exception);
        }
    }

    public async Task<ApiLoginResult> RestoreSessionAsync(string refreshToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                // A refresh token may have rotated while the caller was reading the credential store.
                return await CompleteSignInAsync(RefreshForm(RefreshToken ?? refreshToken), cancellationToken);
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AuthenticationFailure(exception);
        }
    }

    Task<ApiLoginResult> ITsmApiClient.LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        Task.FromResult(new ApiLoginResult(false, "Use browser sign-in to connect your TradeSkillMaster account."));

    public void Logout()
    {
        Interlocked.Exchange(ref _session, null);
        _addons = [];
    }

    private async Task<ApiLoginResult> CompleteSignInAsync(Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var session = await ExchangeAsync(form, cancellationToken);
        Volatile.Write(ref _session, session);
        return new ApiLoginResult(true, session.IsPremium ? "Signed in · Premium" : "Signed in");
    }

    private async Task<Session> GetSessionAsync(CancellationToken cancellationToken)
    {
        var session = RequireSession();
        if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(90))
            return session;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            session = RequireSession();
            if (session.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(90))
                return session;
            Session refreshed;
            try
            {
                refreshed = await ExchangeAsync(RefreshForm(session.RefreshToken), cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
            {
                Interlocked.CompareExchange(ref _session, null, session);
                throw;
            }
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, refreshed, session), session))
                throw new HttpRequestException("The TradeSkillMaster account changed during sync.", null,
                    HttpStatusCode.Unauthorized);
            if (refreshed.RefreshToken != session.RefreshToken)
                RefreshTokenChanged?.Invoke(refreshed.RefreshToken);
            return refreshed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private Session RequireSession() => Volatile.Read(ref _session) ??
        throw new HttpRequestException("Sign in to TradeSkillMaster first.", null, HttpStatusCode.Unauthorized);

    private async Task<Session> ExchangeAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        form["client_id"] = TsmBrowserSignIn.ClientId;
        using var body = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync(TsmBrowserSignIn.OpenId + "/token", body, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new HttpRequestException("TradeSkillMaster rejected the sign-in session.", null,
                HttpStatusCode.Unauthorized);
        response.EnsureSuccessStatusCode();

        var tokens = await ReadAsync<TokenResponse>(response, cancellationToken);
        var refreshToken = tokens.RefreshToken ?? form.GetValueOrDefault("refresh_token");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(refreshToken) ||
            tokens.ExpiresIn <= 0)
            throw new JsonException("TradeSkillMaster returned an incomplete sign-in session.");

        // These claims come directly from the TLS-authenticated token endpoint and are used
        // for display and request parameters. The server authorizes all protected operations.
        using var claims = ReadAccessTokenClaims(tokens.AccessToken);
        var root = claims.RootElement;
        var subject = root.GetProperty("sub").GetString();
        if (string.IsNullOrWhiteSpace(subject))
            throw new JsonException("TradeSkillMaster did not return a user ID.");
        var premium = root.TryGetProperty("realm_access", out var realmAccess) &&
            ReadRoles(realmAccess).Any(role => role is "premium-gold" or "premium-platinum");
        var channels = new List<string> { "Release" };
        if (root.TryGetProperty("resource_access", out var resources) &&
            resources.TryGetProperty("app-addon-version", out var addonAccess))
        {
            foreach (var role in ReadRoles(addonAccess).Where(role => role.StartsWith("channel/", StringComparison.Ordinal)))
            {
                var name = role["channel/".Length..];
                if (name.Length > 0 && !channels.Contains(name, StringComparer.OrdinalIgnoreCase))
                    channels.Add(char.ToUpperInvariant(name[0]) + name[1..]);
            }
        }
        return new Session(tokens.AccessToken, refreshToken, subject, premium, channels,
            DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn));
    }

    private static Dictionary<string, string> RefreshForm(string token) => new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = token
    };

    private static JsonDocument ReadAccessTokenClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new JsonException("TradeSkillMaster returned an invalid access token.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
    }

    private static IEnumerable<string> ReadRoles(JsonElement resource) =>
        resource.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(role => role.GetString()).OfType<string>()
            : [];

    private static ApiLoginResult AuthenticationFailure(Exception exception) => new(false, exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Sign in again in your browser.",
        HttpRequestException => "Could not reach the TradeSkillMaster sign-in service.",
        JsonException or FormatException or KeyNotFoundException => "TradeSkillMaster returned an invalid sign-in response.",
        System.ComponentModel.Win32Exception => "Could not open your default browser.",
        _ => exception.Message
    });

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record Session(string AccessToken, string RefreshToken, string Subject, bool IsPremium,
        IReadOnlyList<string> Channels, DateTimeOffset ExpiresAt);
}
