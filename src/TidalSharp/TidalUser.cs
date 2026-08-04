using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace TidalSharp;

[JsonObject(MemberSerialization.OptIn)]
public class TidalUser
{
    [JsonConstructor]
    internal TidalUser(OAuthTokenData data, string? jsonPath, bool isPkce, DateTime? expirationDate)
    {
        _data = data;
        _jsonPath = jsonPath;
        ExpirationDate = expirationDate ?? DateTime.MinValue;
        IsPkce = isPkce;
    }

    internal async Task GetSession(API api, CancellationToken token = default)
    {
        JObject result = await api.Call(HttpMethod.Get, "sessions", token: token);

        try
        {
            _sessionInfo = result.ToObject<SessionInfo>();
        }
        catch
        {
            throw new APIException("Invalid response for session info.");
        }
    }

    internal async Task RefreshOAuthTokenData(OAuthTokenData data, CancellationToken token = default)
    {
        if (_data == null)
            throw new InvalidOperationException("Attempting to refresh a user with no existing data.");

        _data.AccessToken = data.AccessToken;
        _data.ExpiresIn = data.ExpiresIn;

        DateTime now = DateTime.UtcNow;
        ExpirationDate = now.AddSeconds(data.ExpiresIn);

        await WriteToFile(token);
    }

    internal void UpdateJsonPath(string? jsonPath) => _jsonPath = jsonPath;

    internal async Task WriteToFile(CancellationToken token = default)
    {
        if (_jsonPath != null)
            await File.WriteAllTextAsync(_jsonPath, JsonConvert.SerializeObject(this), token);
    }

    private string? _jsonPath;

    [JsonProperty("Data")]
    private OAuthTokenData _data;
    private SessionInfo? _sessionInfo;

    [JsonProperty("ExpirationDate")]
    public DateTime ExpirationDate { get; private set; } = DateTime.MinValue;

    [JsonProperty("IsPkce")]
    public bool IsPkce { get; init; }

    public string AccessToken => _data.AccessToken;
    public string RefreshToken => _data.RefreshToken;
    public string TokenType => _data.TokenType;

    public long UserId => _data.UserId;

    // _sessionInfo is NOT serialised, so it is null on every restart until
    // GetSession() has run — and GetSession() is called *after* the user is
    // installed as the active one (Core.CheckForStoredUser / Core.Login), and
    // may throw, in which case a user with no session info stays installed.
    // API.Call() copies this straight into the countryCode query parameter, and
    // Tidal rejects an EMPTY countryCode with "countryCode parameter missing"
    // rather than defaulting it — which surfaced as intermittent
    // APIException on GetAlbum during indexer parsing, silently dropping search
    // results. The OAuth payload persisted in lastUser.json already carries the
    // account's country, so fall back to it: same value, always available.
    public string CountryCode
    {
        get
        {
            var sessionCountry = _sessionInfo?.CountryCode;
            if (!string.IsNullOrEmpty(sessionCountry))
                return sessionCountry;
            return _data?.User?.CountryCode ?? "";
        }
    }

    // Deliberately NOT given the same fallback: Core.IsLoggedIn() treats an
    // empty SessionID as "not logged in" to trigger a re-login, and the OAuth
    // payload has no session id to substitute anyway.
    public string SessionID => _sessionInfo?.SessionId ?? "";
}
