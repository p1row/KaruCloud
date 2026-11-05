using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KaruCloud
{
    /// <summary>Minimal Discord Rich Presence client over the local named pipe (no NuGet dependency).</summary>
    public sealed class DiscordRpc : IDisposable
    {
        /// <summary>The one Discord application every KaruCloud user shares (like Bloxstrap): nobody has to create or paste their own.</summary>
        public const string AppId = "1227839273932165151";

        string _clientId = "";
        CancellationTokenSource? _cts;
        NamedPipeClientStream? _pipe;
        readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        Dictionary<string, object>? _last;
        volatile bool _ready;

        public string Status { get; private set; } = "off"; // off | waiting | connecting | ready | error
        public string User { get; private set; } = "";
        public event Action? Changed;

        void Set(string status, string user = "")
        {
            Status = status; User = user;
            Changed?.Invoke();
        }

        public void SetClientId(string id)
        {
            id = (id ?? "").Trim();
            if (id == _clientId) return;
            _clientId = id;
            Stop();
            if (id.Length == 0) { Set("off"); return; }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(id, ct));
        }

        void Stop()
        {
            _ready = false;
            try { _cts?.Cancel(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null; _cts = null;
        }

        async Task RunAsync(string id, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Set("waiting");
                    NamedPipeClientStream? p = null;
                    for (int i = 0; i < 10 && p == null; i++)
                    {
                        var c = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                        try { await c.ConnectAsync(250, ct); p = c; }
                        catch (OperationCanceledException) { c.Dispose(); throw; }
                        catch { c.Dispose(); }
                    }
                    if (p == null) { await Task.Delay(5000, ct); continue; }

                    _pipe = p;
                    Set("connecting");
                    await WriteAsync(p, 0, JsonSerializer.Serialize(new Dictionary<string, object> { ["v"] = 1, ["client_id"] = id }));

                    var header = new byte[8];
                    while (!ct.IsCancellationRequested)
                    {
                        await p.ReadExactlyAsync(header.AsMemory(0, 8), ct);
                        int op = BitConverter.ToInt32(header, 0);
                        int len = BitConverter.ToInt32(header, 4);
                        if (len < 0 || len > 1 << 20) throw new InvalidDataException();
                        var body = new byte[len];
                        if (len > 0) await p.ReadExactlyAsync(body.AsMemory(0, len), ct);
                        var json = Encoding.UTF8.GetString(body);

                        if (op == 3) { await WriteAsync(p, 4, json); continue; }          // ping -> pong
                        if (op == 2) { Set("error"); break; }                              // closed by Discord (bad client id)

                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        string evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                        if (evt == "READY")
                        {
                            string user = "";
                            if (root.TryGetProperty("data", out var d) && d.TryGetProperty("user", out var u))
                            {
                                if (u.TryGetProperty("global_name", out var gn) && gn.ValueKind == JsonValueKind.String) user = gn.GetString() ?? "";
                                if (user.Length == 0 && u.TryGetProperty("username", out var un)) user = un.GetString() ?? "";
                            }
                            _ready = true;
                            Set("ready", user);
                            var last = _last;
                            if (last != null) _ = SendActivityAsync(last);
                        }
                        else if (evt == "ERROR") Set("error");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }

                _ready = false;
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
                var wait = Status == "error" ? 15000 : 5000;
                if (Status != "error") Set("waiting");
                try { await Task.Delay(wait, ct); } catch { break; }
            }
        }

        async Task WriteAsync(NamedPipeClientStream p, int op, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var buf = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(buf, 0);
            BitConverter.GetBytes(body.Length).CopyTo(buf, 4);
            body.CopyTo(buf, 8);
            await _writeLock.WaitAsync();
            try { await p.WriteAsync(buf, 0, buf.Length); await p.FlushAsync(); }
            finally { _writeLock.Release(); }
        }

        async Task SendActivityAsync(Dictionary<string, object>? activity)
        {
            var p = _pipe;
            if (p == null || !_ready) return;
            var args = new Dictionary<string, object> { ["pid"] = Environment.ProcessId };
            if (activity != null) args["activity"] = activity;
            var payload = new Dictionary<string, object>
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = args,
                ["nonce"] = Guid.NewGuid().ToString(),
            };
            try { await WriteAsync(p, 1, JsonSerializer.Serialize(payload)); } catch { }
        }

        /// <summary>Pass null to clear the presence.</summary>
        public void SetActivity(Dictionary<string, object>? activity)
        {
            _last = activity;
            if (_ready) _ = SendActivityAsync(activity);
        }

        public void Dispose() => Stop();
    }
}
