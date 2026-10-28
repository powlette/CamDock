using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace CamDock.Core;

internal sealed record CameraInfo(string Id, string Name, string State, bool IsDoorbell)
{
    public bool IsOnline => string.Equals(State, "CONNECTED", StringComparison.OrdinalIgnoreCase);
}

internal sealed record MetaInfo(string ApplicationVersion);

internal sealed record StreamInfo(string Quality, string PlayUrl, string ApiUrl);

internal enum ApiErrorKind { InvalidUrl, HostNotFound, Refused, Unreachable, Timeout, Tls, Unauthorized, NotFound, BadResponse, Other }

internal sealed class ProtectApiException : Exception
{
    public ApiErrorKind Kind { get; }
    public ProtectApiException(ApiErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

internal static class UrlHelper
{
    /// <summary>Accepts "192.168.1.1", "nvr.local:443", "https://host/anything" and returns "https://host[:port]".</summary>
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var s = (input ?? "").Trim();
        if (s.Length == 0) { error = "Enter the address of your UniFi console (IP address or host name)."; return false; }
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = $"'{input}' is not a valid IP address or URL.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "The address must start with https:// (UniFi consoles use HTTPS).";
            return false;
        }
        if (Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
        {
            error = $"'{uri.Host}' is not a valid host name or IP address.";
            return false;
        }
        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

/// <summary>Client for the UniFi Protect Integration API (X-API-KEY authentication).</summary>
internal sealed class ProtectApi : IDisposable
{
    private const string BasePath = "/proxy/protect/integration/v1";
    private static int _tlsWarned;

    private readonly HttpClient _http;
    public string NvrUrl { get; }
    public string ApiKey { get; }

    public ProtectApi(string nvrUrl, string apiKey)
    {
        NvrUrl = nvrUrl.TrimEnd('/');
        ApiKey = apiKey;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            AutomaticDecompression = DecompressionMethods.All,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = AcceptAnyCertificate },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(NvrUrl + "/"), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CamDock/1.0");
    }

    /// <summary>Certificate errors are ignored by design (self-signed consoles); they are logged once.</summary>
    internal static bool AcceptAnyCertificate(object? sender, System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None && Interlocked.Exchange(ref _tlsWarned, 1) == 0)
            Log.Info($"NVR certificate not trusted ({errors}); ignoring as configured. Subject={cert?.Subject}, Expires={cert?.GetExpirationDateString()}");
        return true;
    }

    public Uri EventsWebSocketUri => WsUri("/subscribe/events");
    public Uri DevicesWebSocketUri => WsUri("/subscribe/devices");

    private Uri WsUri(string path)
    {
        var b = new UriBuilder(NvrUrl + BasePath + path);
        b.Scheme = b.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        return b.Uri;
    }

    /// <summary>
    /// Shows a message on a doorbell's screen. <paramref name="type"/> is LEAVE_PACKAGE_AT_DOOR, DO_NOT_DISTURB or
    /// CUSTOM_MESSAGE (with <paramref name="text"/>, max 30 chars); null clears the screen.
    /// </summary>
    public async Task SetDoorbellMessageAsync(string cameraId, string? type, string? text, TimeSpan duration, CancellationToken ct = default)
    {
        object message = type == null
            ? new { type = "DO_NOT_DISTURB", resetAt = 0L } // a resetAt in the past clears the message
            : type == "CUSTOM_MESSAGE"
                ? new { type, text = (text ?? "").Length > 30 ? text![..30] : text, resetAt = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds() }
                : new { type, resetAt = DateTimeOffset.UtcNow.Add(duration).ToUnixTimeMilliseconds() };
        var body = JsonSerializer.Serialize(new { lcdMessage = message });
        using var _ = await SendJsonAsync(new HttpMethod("PATCH"), $"{BasePath}/cameras/{Uri.EscapeDataString(cameraId)}", body,
            "The API key isn't allowed to change the doorbell message (it needs write access).", ct);
    }

    public async Task<MetaInfo> GetMetaAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(BasePath + "/meta/info", ct);
        var ver = doc.RootElement.TryGetProperty("applicationVersion", out var v) ? v.GetString() ?? "" : "";
        return new MetaInfo(ver);
    }

    public async Task<List<CameraInfo>> GetCamerasAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(BasePath + "/cameras", ct);
        var list = new List<CameraInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ProtectApiException(ApiErrorKind.BadResponse, "Unexpected camera list format from the NVR.");
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var id = c.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            var name = c.TryGetProperty("name", out var n) ? n.GetString() : null;
            var state = c.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            // Protect exposes doorbell capability differently across versions; any of these is a doorbell.
            var doorbell = (c.TryGetProperty("lcdMessage", out var lcd) && lcd.ValueKind == JsonValueKind.Object)
                        || (c.TryGetProperty("hasPackageCamera", out var pk) && pk.ValueKind == JsonValueKind.True)
                        || (c.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && (ty.GetString() ?? "").Contains("Doorbell", StringComparison.OrdinalIgnoreCase));
            if (c.TryGetProperty("featureFlags", out var ff) && ff.ValueKind == JsonValueKind.Object)
            {
                doorbell |= (ff.TryGetProperty("isDoorbell", out var d) && d.ValueKind == JsonValueKind.True)
                         || (ff.TryGetProperty("hasChime", out var ch) && ch.ValueKind == JsonValueKind.True);
            }
            list.Add(new CameraInfo(id!, string.IsNullOrWhiteSpace(name) ? id! : name!, state, doorbell));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    public async Task<byte[]?> GetSnapshotAsync(string cameraId, CancellationToken ct = default, bool highQuality = false)
    {
        try
        {
            var q = highQuality ? "?highQuality=true" : "";
            using var resp = await _http.GetAsync($"{BasePath.TrimStart('/')}/cameras/{Uri.EscapeDataString(cameraId)}/snapshot{q}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snapshot for {cameraId} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns a playable stream URL for the camera, enabling the RTSPS stream through the API if needed
    /// (that needs an API key with write access). Falls back to any quality that is already enabled.
    /// </summary>
    public async Task<StreamInfo> GetStreamAsync(string cameraId, string preferredQuality, bool encrypted, CancellationToken ct = default)
    {
        var path = $"{BasePath}/cameras/{Uri.EscapeDataString(cameraId)}/rtsps-stream";
        var streams = ParseStreams(await SendJsonAsync(HttpMethod.Get, path, null, null, ct));
        var order = new[] { preferredQuality, "high", "medium", "low" }.Distinct().ToList();

        if (!streams.ContainsKey(preferredQuality))
        {
            try
            {
                var body = JsonSerializer.Serialize(new { qualities = new[] { preferredQuality } });
                var created = ParseStreams(await SendJsonAsync(HttpMethod.Post, path, body,
                    "The API key isn't allowed to turn on the camera's RTSPS stream.", ct));
                foreach (var kv in created) streams[kv.Key] = kv.Value;
                Log.Info($"Enabled RTSPS '{preferredQuality}' stream for camera {cameraId}.");
            }
            catch (ProtectApiException ex) when (streams.Count > 0)
            {
                Log.Warn($"Couldn't enable '{preferredQuality}' stream for {cameraId} ({ex.Message}); using an existing stream.");
            }
            catch (ProtectApiException ex) when (ex.Kind is ApiErrorKind.Unauthorized or ApiErrorKind.BadResponse or ApiErrorKind.NotFound)
            {
                throw new ProtectApiException(ex.Kind,
                    "This camera has no RTSPS stream enabled, and the viewer couldn't enable one: " + ex.Message +
                    "\n\nEither create an API key with write (full management) access, or in UniFi Protect open the camera's " +
                    "Settings › Advanced and turn on an RTSPS stream (High, Medium or Low).", ex);
            }
        }

        foreach (var q in order)
        {
            if (streams.TryGetValue(q, out var url)) return new StreamInfo(q, BuildPlayableUrl(url, encrypted), url);
        }
        if (streams.Count == 0)
            throw new ProtectApiException(ApiErrorKind.BadResponse,
                "The NVR didn't return a stream for this camera. In UniFi Protect, open the camera's Settings › Advanced and turn on an RTSPS stream.");
        var any = streams.First();
        return new StreamInfo(any.Key, BuildPlayableUrl(any.Value, encrypted), any.Value);
    }

    private static Dictionary<string, string> ParseStreams(JsonDocument doc)
    {
        using (doc)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } u) d[p.Name] = u;
            return d;
        }
    }

    /// <summary>
    /// Protect returns rtsps://&lt;console&gt;:7441/&lt;alias&gt;?enableSrtp. The host is replaced with the configured
    /// NVR address (multi-homed consoles may report an unreachable interface). Unencrypted mode uses Protect's
    /// plain RTSP listener on port 7447, which every decoder supports.
    /// </summary>
    private string BuildPlayableUrl(string apiUrl, bool encrypted)
    {
        var u = new Uri(apiUrl);
        var host = new Uri(NvrUrl).Host;
        var alias = u.AbsolutePath.Trim('/');
        return encrypted
            ? $"rtsps://{host}:{(u.IsDefaultPort ? 7441 : u.Port)}/{alias}"
            : $"rtsp://{host}:7447/{alias}";
    }

    private Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, path, null, null, ct);

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, string? jsonBody, string? forbiddenMessage, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(method, path.TrimStart('/'));
            if (jsonBody != null) req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not ProtectApiException)
        {
            throw Translate(ex, UrlHelper.HostOf(NvrUrl));
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Forbidden && forbiddenMessage != null)
                throw new ProtectApiException(ApiErrorKind.Unauthorized, forbiddenMessage);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProtectApiException(ApiErrorKind.Unauthorized, "The NVR rejected the API key. Check that it was copied correctly and hasn't been revoked.");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                throw new ProtectApiException(ApiErrorKind.NotFound,
                    "The UniFi Protect Integration API wasn't found at this address. Check it's a UniFi console running Protect 5.3 or later.");
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new ProtectApiException(ApiErrorKind.BadResponse,
                    $"The NVR returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}." + (err.Length is > 0 and < 300 ? $" {err}" : ""));
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            try { return JsonDocument.Parse(body); }
            catch (JsonException ex)
            {
                throw new ProtectApiException(ApiErrorKind.BadResponse, "The address responded, but not like a UniFi Protect console.", ex);
            }
        }
    }

    internal static ProtectApiException Translate(Exception ex, string host)
    {
        if (ex is TaskCanceledException or TimeoutException)
            return new ProtectApiException(ApiErrorKind.Timeout, $"The NVR at {host} didn't respond in time. It may be offline or blocked by a firewall.", ex);
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        new ProtectApiException(ApiErrorKind.HostNotFound, $"The host name '{host}' couldn't be resolved. Check the address.", ex),
                    SocketError.ConnectionRefused =>
                        new ProtectApiException(ApiErrorKind.Refused, $"{host} refused the connection. Check the address and that the console is running.", ex),
                    SocketError.TimedOut =>
                        new ProtectApiException(ApiErrorKind.Timeout, $"Connecting to {host} timed out. The NVR may be offline.", ex),
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                        new ProtectApiException(ApiErrorKind.Unreachable, $"{host} is unreachable. Check your network connection.", ex),
                    _ => new ProtectApiException(ApiErrorKind.Unreachable, $"Network error contacting {host}: {se.Message}", ex),
                };
            }
            if (e is AuthenticationException)
                return new ProtectApiException(ApiErrorKind.Tls, $"A secure (HTTPS) connection to {host} couldn't be established: {e.Message}", ex);
        }
        return new ProtectApiException(ApiErrorKind.Other, $"Couldn't contact {host}: {ex.Message}", ex);
    }

    public void Dispose() => _http.Dispose();
}
