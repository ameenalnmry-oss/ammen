using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using PharmaLIMS.TrustedSigning;

void Annotate(string label, Exception cause) {
    var message = label + ": " + cause.GetType().FullName + ": " + cause.Message;
    var escaped = message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
    Console.WriteLine("::error title=Infrastructure review regression::" + escaped);
}
AppDomain.CurrentDomain.UnhandledException += (_, failure) => {
    if (failure.ExceptionObject is Exception exception)
        Annotate("Unhandled transport failure", exception.GetBaseException());
};
var failures = new List<string>();
async Task Test(string name, Func<Task> test) {
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) {
        failures.Add(name);
        var cause = ex.GetBaseException();
        var diagnostic = name + ": " + cause.GetType().FullName + ": " + cause.Message;
        Console.WriteLine("FAIL " + diagnostic);
        // GitHub annotations must escape workflow-command message characters.
        // These tests use synthetic identities/payloads and contain no credentials.
        Annotate(name, cause);
    }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
await Test("Gregorian report timestamp under Hijri culture", () => {
    var formatter = Assembly.GetExecutingAssembly().GetType("PharmaLIMS.Infrastructure.PreflightTimestamp");
    Check(formatter is not null, "Missing stable preflight timestamp formatter");
    var format = formatter!.GetMethod("Format", BindingFlags.Public|BindingFlags.Static)!;
    var value = new DateTime(2026,10,27,16,20,31,DateTimeKind.Local);
    var original = CultureInfo.CurrentCulture;
    try {
        foreach (var culture in new[] { "ar-SA", "th-TH", "en-US" }) {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Check((string)format.Invoke(null, new object[] { value })! == "2026-10-27 16:20:31", "Report date depends on current calendar");
            Check(value.Ticks == new DateTime(2026,10,27,16,20,31,DateTimeKind.Local).Ticks, "Timestamp instant changed");
        }
    } finally { CultureInfo.CurrentCulture = original; }
    return Task.CompletedTask;
});
await Test("Configured SID admission preserves independent application identities", () => {
    var original = new Dictionary<string,string> { ["S-1-5-21-1-2-3-1001"] = "native-reviewer", ["S-1-5-21-1-2-3-1002"] = "native-service" };
    var map = new TrustedCallerMap(original);
    var admission = typeof(TrustedCallerMap).GetMethod("IsAllowedCaller");
    Check(admission is not null, "Missing configured Windows caller admission");
    bool Allowed(string sid) => (bool)admission!.Invoke(map, new object[] { sid, "S-1-5-21-1-2-3-1002" })!;
    Check(Allowed("S-1-5-21-1-2-3-1001"), "Configured native caller was denied");
    Check(!Allowed("S-1-5-21-1-2-3-9999"), "Unconfigured caller admitted");
    Check(!Allowed("S-1-5-21-1-2-3-1002"), "Same-account caller admitted");
    original["S-1-5-21-1-2-3-9999"] = "injected";
    Check(!Allowed("S-1-5-21-1-2-3-9999"), "Mutable settings changed running admission");
    Check(map.Resolve("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002", "native-reviewer") == "native-reviewer", "Native account mapping changed");
    try { map.Resolve("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002", "ticket-user"); throw new Exception("Native signer identity bypass"); } catch (UnauthorizedAccessException) { }
    return Task.CompletedTask;
});
await Test("Incomplete pipe connections do not serialize callers and expire", async () => {
    var pool = Assembly.GetExecutingAssembly().GetType("PharmaLIMS.TrustedSigning.TrustedPipeConnectionPool");
    Check(pool is not null, "Missing bounded concurrent listener pool");
    var run = pool!.GetMethod("RunAsync", BindingFlags.Public|BindingFlags.Static)!;
    var count = (int)pool.GetField("MaximumConnections")!.GetRawConstantValue()!;
    Check(count >= 2 && count <= 8, "Unbounded or serialized transport");
    var name = "pr62-pipe-" + Guid.NewGuid().ToString("N");
    var pipes = Enumerable.Range(0,count).Select(i => new NamedPipeServerStream(name,PipeDirection.InOut,count,PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (OperatingSystem.IsWindows() && i == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None))).ToArray();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Func<NamedPipeServerStream,CancellationToken,Task> serve = async (pipe, token) => {
        var data = new byte[1];
        await pipe.ReadExactlyAsync(data,token);
        await pipe.WriteAsync(data,token);
    };
    var running = (Task)run.Invoke(null, new object[] { pipes, serve, stop.Token, ready })!;
    var clients = new List<NamedPipeClientStream>();
    try {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var abandoned = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous); clients.Add(abandoned);
        await abandoned.ConnectAsync(stop.Token);
        var good = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous); clients.Add(good);
        await good.ConnectAsync(stop.Token); await good.WriteAsync(new byte[] { 42 },stop.Token);
        var response = new byte[1]; await good.ReadExactlyAsync(response,stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(response[0] == 42, "Incomplete connection blocked valid caller");
        // The abandoned client has sent no frame. Its actual stream must close at the fixed deadline.
        int ended = await abandoned.ReadAsync(new byte[1],stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(18));
        Check(ended == 0, "Incomplete handshake retained a slot beyond deadline");
        if (OperatingSystem.IsWindows()) {
            Check(pipes.All(pipe => !pipe.SafePipeHandle.IsClosed), "Listener handle was destroyed between clients");
            try {
                using var squatter = new NamedPipeServerStream(name,PipeDirection.InOut,count,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance);
                throw new Exception("First pipe ownership was released between clients");
            } catch(IOException) { }
            Console.WriteLine("PASS Windows first-instance ownership survives client disconnect and deadline");
        } else Console.WriteLine("SKIP Windows first-instance ownership: requires native Windows");
        using var next = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await next.ConnectAsync(stop.Token); await next.WriteAsync(new byte[] { 21 },stop.Token);
        await next.ReadExactlyAsync(response,stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(response[0] == 21, "Listener pool did not accept another request after deadline");
    } finally {
        stop.Cancel(); foreach(var client in clients) client.Dispose();
        try { await running; } catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        foreach(var pipe in pipes) pipe.Dispose();
    }
});
await Test("Completed replies survive delayed consumption before pipe reuse", async () => {
    var name = "pr62-pipe-drain-" + Guid.NewGuid().ToString("N");
    var pipes = Enumerable.Range(0,TrustedPipeConnectionPool.MaximumConnections)
        .Select(_ => new NamedPipeServerStream(name,PipeDirection.InOut,TrustedPipeConnectionPool.MaximumConnections,
            // Match WindowsTrustedPipeServer's explicit frame-sized buffers. A
            // zero-quota Windows pipe can hold WriteAsync until the client reads,
            // which deadlocks this deliberate "write, delay, then read" scenario.
            PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536)).ToArray();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var written = new TaskCompletionSource<NamedPipeServerStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    byte[] expected = System.Text.Encoding.UTF8.GetBytes("SameAccountDenied");
    var running = TrustedPipeConnectionPool.RunAsync(pipes, async (pipe,token) => {
        await pipe.WriteAsync(expected,token);
        written.TrySetResult(pipe);
    },stop.Token);
    using var client = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
    try {
        await client.ConnectAsync(stop.Token);
        NamedPipeServerStream listener;
        try { listener = await written.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException exception) {
            throw new Exception("Buffered server reply did not complete before delayed client consumption.", exception);
        }
        // Deliberately leave the reply in the OS buffer after the handler returns.
        // DisconnectNamedPipe would discard it before a scheduled Windows caller reads.
        await Task.Delay(200,stop.Token);
        Check(listener.IsConnected, "Server disconnected before the peer consumed its completed reply");
        var actual = new byte[expected.Length];
        await client.ReadExactlyAsync(actual,stop.Token);
        Check(actual.SequenceEqual(expected), "Buffered refusal reply was truncated before client consumption");
    }
    finally {
        client.Dispose(); stop.Cancel();
        try { await running; } catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        foreach(var pipe in pipes) pipe.Dispose();
    }
});
if(failures.Count != 0) throw new Exception($"{failures.Count} infrastructure regressions failed: {string.Join(", ",failures)}");
