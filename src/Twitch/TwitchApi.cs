using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LiteDrops.Twitch;

public sealed record TwitchUser(string Id, string Login);

public sealed class TwitchApiException(string message) : Exception(message);

public sealed class AuthExpiredException() : Exception("The login has expired");

/// <summary>
/// Minimal Twitch client. It identifies as the Twitch Android app: that is the client whose
/// tokens get the full drops page from GQL, and whose browser login (implicit grant) still works.
/// </summary>
public sealed class TwitchApi : IDisposable
{
    public const string ClientId = "kd1unb4b3q4t58fwlpcbzcbnm76a8fp";
    const string ClientUrl = "https://www.twitch.tv";
    // keep the app version in step with the current Twitch Android release
    const string UserAgent =
        "Dalvik/2.1.0 (Linux; U; Android 16; SM-S911B Build/TP1A.220624.014) tv.twitch.android.app/31.8.0/3108006";
    static readonly Uri GqlUri = new("https://gql.twitch.tv/gql");
    static readonly Uri SpadeUri = new("https://spade.twitch.tv/track");
    static readonly TimeSpan MinGqlSpacing = TimeSpan.FromMilliseconds(250);

    readonly HttpClient _http;
    readonly string _deviceId;
    readonly string _sessionId = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..8]);
    readonly SemaphoreSlim _gqlGate = new(1, 1);
    DateTime _lastGql = DateTime.MinValue;

    public string? AccessToken { get; set; }

    public TwitchApi(string deviceId)
    {
        _deviceId = deviceId;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 4,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        // the app's user agent isn't a valid product list for ParseAdd (the "tv.twitch.android.app/..." part)
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
    }

    public HttpClient Http => _http;

    // ---------------------------------------------------------------- login

    /// <summary>
    /// Page that logs in and sends the browser on to twitch.tv with the access token in the
    /// address. The Android app client only accepts exactly this redirect.
    /// </summary>
    public static string AuthorizeUrl =>
        $"https://id.twitch.tv/oauth2/authorize?client_id={ClientId}" +
        $"&redirect_uri={Uri.EscapeDataString(ClientUrl + "/")}&response_type=token&scope=";

    static readonly Regex TokenInUrl = new(@"access_token=([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
    static readonly Regex BareToken = new("^[A-Za-z0-9]{30}$");

    /// <summary>Reads the access token from the address the browser ended up on (or a bare token).</summary>
    public static string? ParseToken(string text)
    {
        var match = TokenInUrl.Match(text);
        if (match.Success) return match.Groups[1].Value;
        var bare = text.Trim();
        return BareToken.IsMatch(bare) ? bare : null;
    }

    /// <summary>Returns the token's user, or null if the token is no longer valid.</summary>
    public async Task<TwitchUser?> ValidateAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(AccessToken)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", AccessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
        if ((string?)json?["client_id"] != ClientId) return null;
        return new TwitchUser((string)json!["user_id"]!, (string)json["login"]!);
    }

    // ---------------------------------------------------------------- GQL

    public async Task<JsonNode> GqlAsync(JsonObject operation, CancellationToken ct)
        => (await GqlBatchAsync([operation], ct))[0];

    public async Task<IReadOnlyList<JsonNode>> GqlBatchAsync(IReadOnlyList<JsonObject> operations, CancellationToken ct)
    {
        var body = new JsonArray(operations.Select(o => (JsonNode)o.DeepClone()).ToArray());
        for (var attempt = 0; ; attempt++)
        {
            await _gqlGate.WaitAsync(ct);
            try
            {
                var wait = _lastGql + MinGqlSpacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastGql = DateTime.UtcNow;
            }
            finally { _gqlGate.Release(); }

            string? problem;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, GqlUri)
                {
                    Content = JsonContent.Create(body),
                };
                req.Headers.Add("Client-Id", ClientId);
                req.Headers.Add("Client-Session-Id", _sessionId);
                req.Headers.Add("X-Device-Id", _deviceId);
                req.Headers.Add("Origin", ClientUrl);
                req.Headers.Add("Referer", ClientUrl + "/");
                if (!string.IsNullOrEmpty(AccessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", AccessToken);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new AuthExpiredException();
                if ((int)resp.StatusCode >= 500)
                {
                    problem = $"HTTP {(int)resp.StatusCode}";
                }
                else
                {
                    var result = await resp.Content.ReadFromJsonAsync<JsonArray>(ct)
                        ?? throw new TwitchApiException("Empty GQL response");
                    problem = FindProblem(result);
                    if (problem is null) return result.Select(n => n!).ToList();
                }
            }
            catch (HttpRequestException ex) { problem = ex.Message; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { problem = "timeout"; }

            if (attempt >= 4) throw new TwitchApiException($"GQL request failed: {problem}");
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
        }
    }

    // ---------------------------------------------------------------- analytics

    /// <summary>Sends an analytics event the way the web player does; returns whether Twitch took it.</summary>
    public async Task<bool> TrackAsync(string eventName, JsonObject properties, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, SpadeUri)
        {
            Content = new FormUrlEncodedContent([new("data", EncodeEvent(eventName, properties))]),
        };
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Spade takes a base64 encoded JSON array of events.</summary>
    internal static string EncodeEvent(string eventName, JsonObject properties)
    {
        var events = new JsonArray(new JsonObject { ["event"] = eventName, ["properties"] = properties.DeepClone() });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(events.ToJsonString()));
    }

    /// <summary>Game names as Twitch spells them, for the "Add game" suggestions.</summary>
    public async Task<IReadOnlyList<string>> SearchGamesAsync(string query, CancellationToken ct)
    {
        var op = new JsonObject
        {
            ["query"] = "query($q: String!) { searchCategories(query: $q, first: 8) { edges { node { displayName } } } }",
            ["variables"] = new JsonObject { ["q"] = query },
        };
        var result = await GqlAsync(op, ct);
        return (result["data"]?["searchCategories"]?["edges"] as JsonArray ?? [])
            .Select(e => (string?)e?["node"]?["displayName"])
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Returns a description of a retryable error, null if the response is usable.</summary>
    static string? FindProblem(JsonArray result)
    {
        foreach (var item in result)
        {
            if (item?["errors"] is not JsonArray errors || errors.Count == 0) continue;
            var message = (string?)errors[0]?["message"] ?? "unknown error";
            if (message is "PersistedQueryNotFound")
                throw new TwitchApiException("Twitch changed its API (outdated query hash)");
            if (message.Contains("service", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || message.Contains("deadline", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
                return message;
            // other errors are returned to the caller with whatever data came along
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
