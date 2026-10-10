using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Extension.MCP;
using dnSpy.Extension.MCP.Tools;
using dnSpy.Extension.MCP.Transport;
using dnSpy.Extension.MCP.Debugger;

static class Program {
    const int MaxSessions = TransportSessionLimits.MaxSessions;
    static int passed;
    static void Main() {
        Assert(MaxSessions == 100, "logical session capacity must be 100");
        Run("abnormal disconnect accumulation and handshake boundary", AbandonedHandshakes);
        Run("initialized idle accumulation and recent activity", IdleSessions);
        Run("legal GET reconnect and connected session retention", Reconnect);
        Run("handshake timeout closes GET and releases connection capacity", PendingGet);
        Run("in-flight request and body read retain session", ActiveRequest);
        Run("edit/debug leases retained until release", Leases);
        Run("debug lease association, reconnect and closed-request races", DebugAssociations);
        Run("DELETE idempotence and cleanup observer isolation", Delete);
        Run("parallel initialize and reclaim remain bounded", ConcurrentAdmission);
        Run("short and long request capacity rejection diagnostics", AdmissionDiagnostics);
        Run("listener stop closes sessions once and restart admits", StopRestart);
        Console.WriteLine($"PASS: {passed} transport regression groups");
    }
    static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Wait(Func<bool> condition, string message) {
        Assert(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), message);
    }
    static void AbandonedHandshakes() {
        using var h = new Host();
        for (int round = 0; round < 3; round++) {
            var ids = Enumerable.Range(0, MaxSessions).Select(_ => h.Initialize()).ToArray();
            foreach (var id in ids) h.ResetGet(id); // TCP reset, no notifications/initialized or DELETE.
            Wait(() => h.Streams == 0, "abnormal GET disconnect did not release streams");
            h.Advance(29999);
            h.ExpectInitialize(HttpStatusCode.TooManyRequests);
            Assert(h.Count == MaxSessions && h.Events.Count == 0, "premature reclaim before handshake deadline");
            h.Advance(1);
            var fresh = h.Initialize(); // Admission itself must sweep, without waiting for timer.
            Assert(h.Count == 1, "expired sessions still consume the 100 session capacity");
            Assert(ids.All(id => h.ClosedCount(id, "handshake_timeout") == 1), "handshake cleanup must notify exactly once");
            h.Delete(fresh);
            h.Events.Clear();
        }
        Assert(h.Settings.Logs.Any(l => l.Contains("reason=streamable_session_capacity") && l.Contains($"pending_handshakes={MaxSessions}")), "missing capacity diagnostic");
    }
    static void IdleSessions() {
        using var h = new Host();
        var ids = Enumerable.Range(0, MaxSessions).Select(_ => h.Initialize(true)).ToArray();
        h.Advance(599999); h.ExpectInitialize(HttpStatusCode.TooManyRequests);
        h.Ping(ids[0]); h.Advance(1);
        h.Initialize();
        Assert(h.Count == 2, "idle sessions were not reclaimed, or recently used session was lost");
        Assert(h.Sessions.ContainsKey(ids[0]), "recent client request must renew inactivity deadline");
        Assert(ids.Skip(1).All(id => h.ClosedCount(id, "idle_timeout") == 1), "idle cleanup must notify");
        h.Advance(600000); h.Server.ReapStreamableSessions();
        Assert(h.Count == 0, "periodic sweep seam did not clean idle and pending sessions");
    }
    static void Reconnect() {
        using var h = new Host(); var id = h.Initialize(true);
        h.ResetGet(id); Wait(() => h.Streams == 0, "GET did not disconnect");
        Assert(h.Count == 1 && h.Events.Count == 0, "GET disconnect must not end logical session");
        h.Advance(599999); h.Ping(id);
        using (h.Get(id)) {
            h.Advance(600001); h.Server.ReapStreamableSessions();
            Assert(h.Sessions.ContainsKey(id), "connected initialized GET must retain session");
            h.Ping(id);
        }
        Wait(() => h.Streams == 0, "reconnected stream did not disconnect");
        h.Advance(599999); h.Ping(id); // Full reconnect grace after disconnect.
        h.Advance(600000); h.Server.ReapStreamableSessions();
        Assert(h.ClosedCount(id, "idle_timeout") == 1, "disconnected session did not expire");
        h.Ping(id, HttpStatusCode.NotFound);
    }
    static void PendingGet() {
        using var h = new Host(); var id = h.Initialize();
        using var get = h.Get(id);
        h.Advance(30000); h.Server.ReapStreamableSessions();
        Assert(h.ClosedCount(id, "handshake_timeout") == 1, "open GET must not pin incomplete handshake");
        Wait(() => h.Streams == 0, "expired handshake stream was not aborted");
        var next = h.Initialize(true); using var replacement = h.Get(next);
        Assert(replacement.StatusCode == HttpStatusCode.OK, "connection capacity not reusable after expiry");
    }
    static void ActiveRequest() {
        using var h = new Host(); var id = h.Initialize(true);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        h.Registry.OnCall = () => { entered.Set(); Assert(release.Wait(5000), "blocked tool timed out"); return new(); };
        var task = Task.Run(() => h.Post("tools/call", id, "{\"name\":\"block\"}"));
        try {
            Assert(entered.Wait(5000), "tool never entered");
            h.AdvanceWhileBusy(600001); h.Server.ReapStreamableSessions();
            Assert(h.Sessions.ContainsKey(id) && h.Events.Count == 0, "active POST reclaimed");
        } finally { release.Set(); }
        using (var result = task.GetAwaiter().GetResult()) Assert(result.StatusCode == HttpStatusCode.OK, "active POST failed");
        h.Advance(599999); h.Server.ReapStreamableSessions(); Assert(h.Sessions.ContainsKey(id), "idle grace must start at request completion");
        // Reserve before reading a slow or incomplete request body.
        using var tcp = new TcpClient(); tcp.Connect("127.0.0.1", h.Port);
        var bytes = Encoding.ASCII.GetBytes($"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{h.Port}\r\nAccept: application/json, text/event-stream\r\nMcp-Session-Id: {id}\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{{");
        tcp.GetStream().Write(bytes);
        Wait(() => h.ActiveRequests == 1, "body-read reservation missing");
        h.AdvanceWhileBusy(600001); h.Server.ReapStreamableSessions(); Assert(h.Sessions.ContainsKey(id), "body-read request reclaimed");
        tcp.Client.LingerState = new LingerOption(true, 0); tcp.Close();
        Wait(() => h.ActiveRequests == 0, "body-read reservation leaked after disconnect");
        h.Advance(600000); h.Server.ReapStreamableSessions(); Assert(h.Count == 0, "active reservation leaked");
    }
    static void Leases() {
        using var h = new Host();
        var edit = h.Initialize(true); var debug = h.Initialize(true);
        h.Guard.Edit.Add(edit); h.Guard.Debug.Add(debug);
        h.Advance(600000); h.Server.ReapStreamableSessions();
        Assert(h.Count == 2 && h.Events.Count == 0, "edit/debug lease must prevent timeout cleanup");
        for (int i = 0; i < MaxSessions - 2; i++) { var id = h.Initialize(true); h.Guard.Edit.Add(id); }
        h.Advance(600000); h.ExpectInitialize(HttpStatusCode.TooManyRequests);
        Assert(h.Settings.Logs.Any(l => l.Contains($"leased_sessions={MaxSessions}")), "lease rejection diagnostic missing");
        h.Guard.Edit.Remove(edit); h.Server.ReapStreamableSessions();
        Assert(h.ClosedCount(edit, "idle_timeout") == 1 && h.Sessions.ContainsKey(debug), "release must only reclaim unleased owner");
        h.Guard.Debug.Remove(debug); h.Server.ReapStreamableSessions(); Assert(h.ClosedCount(debug, "idle_timeout") == 1, "debug lease never releases");
        h.Guard.Throw = true; h.Advance(600000); h.Server.ReapStreamableSessions(); Assert(h.Count == MaxSessions - 2, "uncertain lease ownership must fail closed");
    }
    static void DebugAssociations() {
        string? debugSession = "debug-1";
        var leases = new DebugTransportSessionLeases(() => debugSession);
        var lifetime = new McpTransportSessionLifetime();
        var owner = McpCallContext.StreamableHttp("owner", "2025-06-18", true, lifetime);
        var reconnect = McpCallContext.StreamableHttp("reconnect", "2025-06-18", true);
        var invalid = McpCallContext.StreamableHttp("invalid", "2025-06-18", true);
        leases.ObserveCall(owner, true); leases.ObserveCall(reconnect, true); leases.ObserveCall(invalid, false);
        Assert(leases.HasSessionLease(McpTransportKind.StreamableHttp, "owner") && leases.HasSessionLease(McpTransportKind.StreamableHttp, "reconnect"), "debug owner and valid reconnect must both be protected");
        Assert(!leases.HasSessionLease(McpTransportKind.StreamableHttp, "invalid"), "rejected unrelated debug call must not acquire lease");
        lifetime.Close(); leases.OnSessionClosed(new(McpTransportKind.StreamableHttp, "owner", "client_delete", 1));
        leases.ObserveCall(owner, true);
        Assert(!leases.HasSessionLease(McpTransportKind.StreamableHttp, "owner"), "post-DELETE completion reinstalled debug lease");
        debugSession = "debug-2";
        Assert(!leases.HasSessionLease(McpTransportKind.StreamableHttp, "reconnect"), "old generation must not protect a later debug session");
        leases.ObserveCall(reconnect, true); debugSession = null;
        Assert(!leases.HasSessionLease(McpTransportKind.StreamableHttp, "reconnect"), "terminal debugger must release transport lease");
    }
    static void Delete() {
        using var h = new Host(); var id = h.Initialize(true); h.Guard.Edit.Add(id);
        using var stream = h.Get(id);
        Parallel.For(0, 8, _ => h.Delete(id));
        Assert(h.Count == 0 && h.ClosedCount(id, "client_delete") == 1, "DELETE must be idempotent despite lease/stream");
        Wait(() => h.Streams == 0, "DELETE did not abort stream");
        h.Delete(id); h.Advance(600000); h.Server.ReapStreamableSessions(); Assert(h.Events.Count == 1, "duplicate close notification");
        var context = h.ClosedContext;
        Assert(context?.IsSessionClosed == true, "closed lifetime must reach in-flight tool contexts");
        Assert(h.Settings.Logs.Any(l => l.Contains("observer failed")), "observer fault not isolated");
    }
    static void ConcurrentAdmission() {
        using var h = new Host();
        Parallel.For(0, MaxSessions * 2, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => h.ExpectInitialize(null));
        Assert(h.Count == MaxSessions, "parallel initialize did not fill bounded capacity");
        h.Advance(30000);
        Parallel.Invoke(h.Server.ReapStreamableSessions, () => Parallel.For(0, MaxSessions * 2, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => h.ExpectInitialize(null)));
        Assert(h.Count == MaxSessions && h.Events.Count == MaxSessions, "parallel reclaim/admission failed boundedness or idempotence");
    }
    static void AdmissionDiagnostics() {
        using var h = new Host(); var id = h.Initialize(true);
        var gets = Enumerable.Range(0, 8).Select(_ => h.Get(id)).ToArray();
        try {
            using var ninth = new HttpRequestMessage(HttpMethod.Get, "mcp");
            ninth.Headers.Accept.ParseAdd("text/event-stream"); ninth.Headers.Add("Mcp-Session-Id", id);
            using var rejected = h.Http.Send(ninth);
            Assert(rejected.StatusCode == HttpStatusCode.TooManyRequests, "ninth GET must be rejected");
            Assert(h.Settings.Logs.Any(l => l.Contains("reason=long_connection_capacity") && l.Contains("long_connections=8/8")), "long gate reason missing");
        } finally { foreach (var get in gets) get.Dispose(); }
        Wait(() => h.LongSlots == 8, "long slots did not release");
        using var entered = new CountdownEvent(16); using var release = new ManualResetEventSlim();
        h.Registry.OnCall = () => { entered.Signal(); Assert(release.Wait(5000), "short gate barrier timeout"); return new(); };
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() => h.Post("tools/call", id, "{\"name\":\"block\"}"), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try {
            Assert(entered.Wait(5000), "16 short requests not admitted");
            h.ExpectInitialize(HttpStatusCode.TooManyRequests);
            Assert(h.Settings.Logs.Any(l => l.Contains("reason=short_request_capacity") && l.Contains("short_requests=16/16")), "short gate reason missing");
        } finally { release.Set(); }
        foreach (var task in tasks) using (var response = task.GetAwaiter().GetResult()) Assert(response.StatusCode == HttpStatusCode.OK, "blocked short request failed");
    }
    static void StopRestart() {
        using var h = new Host(); var a = h.Initialize(true); var b = h.Initialize();
        using var stream = h.Get(a);
        h.Server.Stop(); h.Server.Stop();
        Assert(h.Count == 0 && h.ClosedCount(a, "listener_stop") == 1 && h.ClosedCount(b, "listener_stop") == 1, "stop must notify exactly once");
        h.Server.Start(); h.Initialize(true); Assert(h.Count == 1, "restart cannot admit new session");
    }
    sealed class SessionSet {
        readonly ConcurrentDictionary<string, byte> entries = new();
        public void Add(string id) => entries.TryAdd(id, 0);
        public void Remove(string id) => entries.TryRemove(id, out _);
        public bool Contains(string id) => entries.ContainsKey(id);
    }
    sealed class Guard : IMcpTransportSessionLeaseGuard {
        public readonly SessionSet Edit = new(); public readonly SessionSet Debug = new(); public bool Throw;
        public bool HasSessionLease(McpTransportKind kind, string id) {
            if (Throw) throw new InvalidOperationException("guard failure");
            return Edit.Contains(id) || Debug.Contains(id);
        }
    }
    sealed class Observer : IMcpTransportSessionObserver {
        readonly ConcurrentQueue<McpTransportSessionClosed> events;
        public Observer(ConcurrentQueue<McpTransportSessionClosed> events) => this.events = events;
        public void OnSessionClosed(McpTransportSessionClosed closed) => events.Enqueue(closed);
    }
    sealed class FaultObserver : IMcpTransportSessionObserver {
        public void OnSessionClosed(McpTransportSessionClosed closed) => throw new Exception("intentional observer failure");
    }
    sealed class Host : IDisposable {
        long now;
        public readonly McpSettings Settings;
        public readonly McpServer Server;
        public readonly McpToolRegistry Registry = new();
        public readonly Guard Guard = new();
        public readonly ConcurrentQueue<McpTransportSessionClosed> Events = new();
        public McpCallContext? ClosedContext;
        public readonly HttpClient Http;
        public readonly ConcurrentDictionary<string, StreamableHttpSession> Sessions;
        public int Port => Server.ActualPort;
        public int Count => Sessions.Count;
        public int LongSlots => ((AdmissionGate)typeof(McpServer).GetField("longConnectionGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!).CurrentCount;
        public int Streams { get { lock (Sessions) return Sessions.Values.Sum(s => s.OpenStreams); } }
        public int ActiveRequests { get { lock (Sessions) return Sessions.Values.Sum(s => s.ActiveRequests); } }
        public Host() {
            using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Settings = new McpSettings { Port = port };
            var lifecycle = new McpTransportSessionLifecycle(Settings, new IMcpTransportSessionObserver[] { new FaultObserver(), new Observer(Events) }, new[] { Guard });
            Server = new McpServer(Settings, Registry, new BepInExResources(), lifecycle, () => Interlocked.Read(ref now), 50);
            Sessions = (ConcurrentDictionary<string, StreamableHttpSession>)typeof(McpServer).GetField("streamableSessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
            Server.Start(); Assert(Server.IsRunning, "listener failed to start");
            Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(5) };
        }
        public void Advance(long ms) { Wait(() => ActiveRequests == 0, "requests not settled before clock advance"); AdvanceWhileBusy(ms); }
        public void AdvanceWhileBusy(long ms) { lock (Sessions) Interlocked.Add(ref now, ms); }
        public int ClosedCount(string id, string reason) => Events.Count(e => e.SessionId == id && e.Reason == reason);
        public string Initialize(bool complete = false) {
            using var response = Post("initialize"); Assert(response.StatusCode == HttpStatusCode.OK, $"initialize HTTP {(int)response.StatusCode}");
            var id = response.Headers.GetValues("Mcp-Session-Id").Single();
            lock (Sessions) ClosedContext = Sessions[id].CreateCallContext();
            if (complete) { using var notification = Post("notifications/initialized", id, notification: true); Assert(notification.StatusCode == HttpStatusCode.Accepted, "initialized not accepted"); }
            return id;
        }
        public void ExpectInitialize(HttpStatusCode? expected) {
            using var result = Post("initialize");
            Assert(expected == null ? result.StatusCode == HttpStatusCode.OK || result.StatusCode == HttpStatusCode.TooManyRequests : result.StatusCode == expected, "unexpected initialize status");
            if (result.StatusCode == HttpStatusCode.TooManyRequests) {
                Assert(result.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult().Length == 0 && result.Headers.RetryAfter != null, "429 shape changed");
            }
        }
        public HttpResponseMessage Post(string method, string? id = null, string? args = null, bool notification = false) {
            using var request = Request(HttpMethod.Post, id);
            request.Content = new StringContent("{\"jsonrpc\":\"2.0\"," + (notification ? "" : "\"id\":1,") + "\"method\":\"" + method + "\"" + (args == null ? "" : ",\"params\":" + args) + "}", Encoding.UTF8, "application/json");
            return Http.Send(request);
        }
        HttpRequestMessage Request(HttpMethod method, string? id) {
            var request = new HttpRequestMessage(method, "mcp"); request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (id != null) request.Headers.Add("Mcp-Session-Id", id); return request;
        }
        public void Ping(string id, HttpStatusCode expected = HttpStatusCode.OK) { using var response = Post("ping", id); Assert(response.StatusCode == expected, $"ping HTTP {(int)response.StatusCode}"); }
        public void Delete(string id) { using var request = Request(HttpMethod.Delete, id); using var result = Http.Send(request); Assert(result.StatusCode == HttpStatusCode.OK, "DELETE failed"); }
        public HttpResponseMessage Get(string id) { using var request = Request(HttpMethod.Get, id); var result = Http.Send(request, HttpCompletionOption.ResponseHeadersRead); Assert(result.StatusCode == HttpStatusCode.OK, "GET failed"); return result; }
        public void ResetGet(string id) {
            using var tcp = new TcpClient(); tcp.Connect("127.0.0.1", Port); tcp.ReceiveTimeout = 5000;
            var bytes = Encoding.ASCII.GetBytes($"GET /mcp HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nAccept: text/event-stream\r\nMcp-Session-Id: {id}\r\n\r\n");
            tcp.GetStream().Write(bytes); var buffer = new byte[1024]; var read = tcp.GetStream().Read(buffer); Assert(read > 0 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/1.1 200"), "GET reset probe did not reach production handler: " + Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read)));
            tcp.Client.LingerState = new LingerOption(true, 0); tcp.Close();
            Wait(() => LongSlots == 8, "TCP reset did not release GET admission slot");
        }
        public void Dispose() { Server.Dispose(); Http.Dispose(); }
    }
}
