using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CamDock.Core;

/// <summary>A device change from the devices websocket (only the fields we use).</summary>
internal sealed record DeviceUpdate(string Action, string Id, string ModelKey, string? State);

internal sealed record ProtectEvent(string Action, string Id, string Type, string? DeviceId, IReadOnlyList<string> SmartTypes, DateTime Start);

/// <summary>
/// Listens to the Integration API events websocket (push, no polling) and reconnects with backoff.
/// Callbacks are marshalled onto the UI thread.
/// </summary>
internal sealed class EventListener : IDisposable
{
    private readonly Uri _uri;
    private readonly string _apiKey;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _cts;

    public event Action<ProtectEvent>? EventReceived;
    public event Action<DeviceUpdate>? DeviceReceived;
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected { get; private set; }

    public EventListener(Uri uri, string apiKey, SynchronizationContext ui)
    {
        _uri = uri;
        _apiKey = apiKey;
        _ui = ui;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                ws.Options.SetRequestHeader("X-API-KEY", _apiKey);
                ws.Options.RemoteCertificateValidationCallback = ProtectApi.AcceptAnyCertificate;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(_uri, connectCts.Token);
                }
                Log.Info("Event stream connected.");
                SetConnected(true);
                delay = TimeSpan.FromSeconds(2);
                await ReceiveLoopAsync(ws, ct);
                Log.Info($"Event stream closed by NVR ({ws.CloseStatus} {ws.CloseStatusDescription}).");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Event stream error: {ex.GetBaseException().Message}. Retrying in {delay.TotalSeconds:0}s.");
            }
            SetConnected(false);
            try { await Task.Delay(delay, ct); } catch { break; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
        SetConnected(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                Dispatch(text);
            }
            ms.SetLength(0);
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = root.TryGetProperty("type", out var a) ? a.GetString() ?? "" : "";
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return;
            var modelKey = item.TryGetProperty("modelKey", out var mk) ? mk.GetString() : null;
            if (modelKey != null && modelKey != "event")
            {
                var devId = item.TryGetProperty("id", out var di) ? di.GetString() : null;
                if (string.IsNullOrEmpty(devId)) return;
                var state = item.TryGetProperty("state", out var sv2) && sv2.ValueKind == JsonValueKind.String ? sv2.GetString() : null;
                var upd = new DeviceUpdate(action, devId!, modelKey, state);
                _ui.Post(_ => DeviceReceived?.Invoke(upd), null);
                return;
            }

            var id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            string? device = null;
            if (item.TryGetProperty("device", out var d)) device = d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (device == null && item.TryGetProperty("deviceId", out var d2)) device = d2.GetString();
            var smart = new List<string>();
            if (item.TryGetProperty("smartDetectTypes", out var st) && st.ValueKind == JsonValueKind.Array)
                foreach (var s in st.EnumerateArray()) if (s.GetString() is { } v) smart.Add(v);
            var start = DateTime.Now;
            if (item.TryGetProperty("start", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt64(out var ms))
                start = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

            if (string.IsNullOrEmpty(id)) return;
            var ev = new ProtectEvent(action, id, type, device, smart, start);
            _ui.Post(_ => EventReceived?.Invoke(ev), null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not parse event message: {ex.Message}");
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        _ui.Post(_ => ConnectionChanged?.Invoke(value), null);
    }

    public void Dispose() => Stop();
}
