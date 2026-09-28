using System.Text.Json;
using Microsoft.Extensions.Options;

namespace EDrafter.Api.Zoho;

/// <summary>Supplies the Zoho access token. Ported from the Zoho Sign POC.</summary>
public interface IZohoTokenProvider
{
    Task<string> GetAsync(CancellationToken ct = default);

    /// <summary>Drop the cached token after a 401 so the next call mints a fresh one.</summary>
    void Invalidate();
}

/// <summary>The 1-hour development token pasted into config. For a quick manual try only.</summary>
public sealed class ZohoStaticTokenProvider(IOptions<ZohoOptions> options, ILogger<ZohoStaticTokenProvider> log)
    : IZohoTokenProvider
{
    private readonly ZohoOptions _o = options.Value;

    public Task<string> GetAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_o.DevToken))
            throw new ZohoException("Zoho:DevToken is empty. Set it, or use Zoho:AuthMode = RefreshToken.");

        return Task.FromResult(_o.DevToken);
    }

    public void Invalidate() =>
        log.LogWarning("Zoho rejected the development token — it lasts one hour. Paste a new one, " +
                       "or switch Zoho:AuthMode to RefreshToken.");
}

/// <summary>
/// Mints access tokens from the permanent refresh token and caches them. Zoho access
/// tokens live one hour; this renews five minutes early, behind a gate so a cold start
/// with several callers mints exactly one.
/// </summary>
public sealed class ZohoRefreshTokenProvider(
    IHttpClientFactory httpFactory,
    IOptions<ZohoOptions> options,
    ILogger<ZohoRefreshTokenProvider> log) : IZohoTokenProvider
{
    private static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private readonly ZohoOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (IsFresh()) return _token!;

        await _gate.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _token!;

            (_token, _expiresAt) = await FetchAsync(ct);
            log.LogInformation("Zoho access token refreshed, valid until {Expiry:u}.", _expiresAt);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _token = null;
        _expiresAt = DateTimeOffset.MinValue;
        log.LogWarning("Cached Zoho access token discarded after a 401; the next call mints a new one.");
    }

    private bool IsFresh() => _token is not null && DateTimeOffset.UtcNow < _expiresAt - RenewMargin;

    private async Task<(string Token, DateTimeOffset ExpiresAt)> FetchAsync(CancellationToken ct)
    {
        if (!_o.HasCredentials)
            throw new ZohoException(
                "Zoho:ClientId, Zoho:ClientSecret and Zoho:RefreshToken are required. Put them in " +
                "appsettings.Local.json — the Zoho POC's appsettings.Development.json has them.");

        // Form body, not query string: keeps the client secret out of URLs and proxy logs.
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("refresh_token", _o.RefreshToken),
            new KeyValuePair<string, string>("client_id", _o.ClientId),
            new KeyValuePair<string, string>("client_secret", _o.ClientSecret),
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
        ]);

        using var http = httpFactory.CreateClient();
        using var res = await http.PostAsync($"{_o.AccountsUrl.TrimEnd('/')}/oauth/v2/token", form, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new ZohoException($"Zoho token refresh failed with {(int)res.StatusCode}: {raw}", (int)res.StatusCode);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // HTTP 200 with an "error" key is Zoho's way of failing here too.
        if (root.TryGetProperty("error", out var error))
            throw new ZohoException(
                $"Zoho token refresh rejected: {error.GetString()}. Check the refresh token and the " +
                $"client id/secret, and that all were issued on {_o.AccountsUrl}.");

        var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(token))
            throw new ZohoException("Zoho token refresh returned no access_token.");

        if (root.TryGetProperty("scope", out var scope))
            log.LogInformation("Zoho access token scopes: {Scopes}", scope.GetString());

        var lifetime = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : DefaultLifetime;

        return (token, DateTimeOffset.UtcNow + lifetime);
    }
}
