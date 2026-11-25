using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HttpMcpProxy.Configuration;

namespace HttpMcpProxy.Execution;

/// <summary>Applies the service's single <c>auth</c> block to outgoing requests.</summary>
internal sealed class AuthHandler(AuthConfig auth, HttpClient client, string serviceDirectory)
{
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private CachedToken? _token;

    public bool IsOAuth => auth.Type == "oauth2";

    /// <summary>Adds query parameters required by auth; called while the URL is being built.</summary>
    public void AddQuery(List<KeyValuePair<string, string>> query)
    {
        if (auth.Type == "api_key" && auth.In == "query")
            query.Add(new(auth.Name!, auth.Prefix + auth.Value));
        else if (auth.Type == "custom")
            query.AddRange(auth.Query);
    }

    /// <summary>Adds cookies required by auth.</summary>
    public void AddCookies(List<KeyValuePair<string, string>> cookies)
    {
        if (auth.Type == "api_key" && auth.In == "cookie")
            cookies.Add(new(auth.Name!, auth.Prefix + auth.Value));
        else if (auth.Type == "custom")
            cookies.AddRange(auth.Cookies);
    }

    public async Task ApplyHeadersAsync(HttpRequestMessage request, CancellationToken ct)
    {
        switch (auth.Type)
        {
            case "api_key" when auth.In == "header":
                request.Headers.Remove(auth.Name!);
                request.Headers.TryAddWithoutValidation(auth.Name!, auth.Prefix + auth.Value);
                break;
            case "bearer":
                request.Headers.Remove(auth.Header);
                request.Headers.TryAddWithoutValidation(auth.Header, $"{auth.Scheme} {auth.Token}".Trim());
                break;
            case "basic":
                var raw = Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
                break;
            case "oauth2":
                var token = await GetTokenAsync(ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            case "custom":
                foreach (var (name, value) in auth.Headers)
                {
                    request.Headers.Remove(name);
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                break;
        }
    }

    /// <summary>Drops the cached OAuth token, e.g. after a 401.</summary>
    public void InvalidateToken()
    {
        _token = null;
        if (auth.TokenCache == "file" && CachePath is { } path && File.Exists(path))
            File.Delete(path);
    }

    private string? CachePath => auth.TokenCachePath is { Length: > 0 } p
        ? Path.IsPathRooted(p) ? p : Path.Combine(serviceDirectory, p)
        : null;

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is { } cached && cached.IsValid(auth.RefreshBeforeExpiryS))
            return cached.AccessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } again && again.IsValid(auth.RefreshBeforeExpiryS))
                return again.AccessToken;

            if (auth.TokenCache == "file" && ReadFileCache() is { } fromFile && fromFile.IsValid(auth.RefreshBeforeExpiryS))
                return (_token = fromFile).AccessToken;

            _token = await RequestTokenAsync(ct);
            if (auth.TokenCache == "file")
                WriteFileCache(_token);
            return _token.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<CachedToken> RequestTokenAsync(CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>();
        if (auth.Flow == "refresh_token")
        {
            form.Add(new("grant_type", "refresh_token"));
            form.Add(new("refresh_token", auth.RefreshToken ?? throw new InvalidOperationException("auth.refresh_token is not set")));
        }
        else
        {
            form.Add(new("grant_type", "client_credentials"));
        }

        if (auth.Scopes.Count > 0) form.Add(new("scope", string.Join(' ', auth.Scopes)));
        if (auth.Audience is { Length: > 0 }) form.Add(new("audience", auth.Audience));
        form.AddRange(auth.ExtraParams);

        using var request = new HttpRequestMessage(HttpMethod.Post, auth.TokenUrl);
        if (auth.ClientAuth == "basic")
        {
            var raw = $"{Uri.EscapeDataString(auth.ClientId ?? "")}:{Uri.EscapeDataString(auth.ClientSecret ?? "")}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else
        {
            form.Add(new("client_id", auth.ClientId ?? ""));
            if (auth.ClientSecret is not null) form.Add(new("client_secret", auth.ClientSecret));
        }

        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new AuthException($"OAuth2 token request failed: HTTP {(int)response.StatusCode}");

        var json = JsonNode.Parse(body) as JsonObject ?? throw new AuthException("OAuth2 token response is not a JSON object");
        var accessToken = json["access_token"]?.GetValue<string>() ?? throw new AuthException("OAuth2 token response has no access_token");
        var expiresIn = json["expires_in"] is JsonValue ev && ev.TryGetValue<double>(out var seconds) ? seconds : 3600;
        return new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private CachedToken? ReadFileCache()
    {
        try
        {
            return CachePath is { } path && File.Exists(path) ? JsonSerializer.Deserialize<CachedToken>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteFileCache(CachedToken token)
    {
        if (CachePath is not { } path) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(token));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool IsValid(int refreshBeforeSeconds) => DateTimeOffset.UtcNow < ExpiresAt.AddSeconds(-refreshBeforeSeconds);
    }
}

internal sealed class AuthException(string message) : Exception(message);
