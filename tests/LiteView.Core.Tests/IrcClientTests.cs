using System.Threading.Channels;
using LiteViewCore;
using Xunit;

// One fake connection: the test pushes server messages into it and reads what the client sent.
sealed class FakeConn : IIrcTransport
{
    public readonly Channel<object?> In = Channel.CreateUnbounded<object?>();   // string, null (= server closes) or an Exception
    public readonly List<string> Sent = new List<string>();
    public readonly TaskCompletionSource Joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool open;
    public bool IsOpen => open;
    public Task ConnectAsync(Uri server, CancellationToken ct) { open = true; return Task.CompletedTask; }
    public Task SendAsync(string text, CancellationToken ct)
    {
        lock (Sent) Sent.Add(text.TrimEnd('\r', '\n'));
        if (text.StartsWith("JOIN ", StringComparison.Ordinal)) Joined.TrySetResult();
        return Task.CompletedTask;
    }
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        object? m = await In.Reader.ReadAsync(ct);
        if (m is Exception ex) throw ex;
        if (m == null) open = false;
        return (string?)m;
    }
    public void Abort() { open = false; In.Writer.TryComplete(); }
    public void Dispose() { open = false; }
    public string[] SentLines() { lock (Sent) return Sent.ToArray(); }
}

public class IrcClientTests
{
    readonly List<FakeConn> conns = new List<FakeConn>();
    readonly List<TimeSpan> waits = new List<TimeSpan>();
    readonly SemaphoreSlim newConn = new SemaphoreSlim(0);
    readonly List<string> lines = new List<string>();
    readonly SemaphoreSlim gotLine = new SemaphoreSlim(0);
    int read;
    TimeSpan now = TimeSpan.Zero;
    string? login, token;

    Irc Client()
    {
        var irc = new Irc((out string? l, out string? t) => { l = login; t = token; },
            () => { var c = new FakeConn(); lock (conns) conns.Add(c); newConn.Release(); return c; },
            (d, ct) => { lock (waits) waits.Add(d); return Task.CompletedTask; },
            () => now, new Random(7));
        irc.Line += l => { lock (lines) lines.Add(l); gotLine.Release(); };
        return irc;
    }

    async Task<FakeConn> NextConnection(int n)
    {
        while (true)
        {
            lock (conns) if (conns.Count >= n) { var c = conns[n - 1]; return c; }
            Assert.True(await newConn.WaitAsync(5000), "no connection " + n);
        }
    }

    // the lines in the order they arrived
    async Task<string> NextLine()
    {
        Assert.True(await gotLine.WaitAsync(5000), "no line");
        lock (lines) return lines[read++];
    }

    [Fact]
    public async Task AnonymousHandshake()
    {
        var irc = Client(); irc.Start("somechannel");
        var c = await NextConnection(1); await c.Joined.Task;
        var s = c.SentLines();
        Assert.Equal("CAP REQ :twitch.tv/tags twitch.tv/commands", s[0]);
        Assert.Equal("PASS SCHMOOPIIE", s[1]);
        Assert.Matches("^NICK justinfan[0-9]{5}$", s[2]);
        Assert.Equal("JOIN #somechannel", s[3]);
        irc.Stop();
    }

    [Fact]
    public async Task LoggedInHandshakeAndPong()
    {
        login = "viewer"; token = "tok123";
        var irc = Client(); irc.Start("chan");
        var c = await NextConnection(1); await c.Joined.Task;
        Assert.Contains("PASS oauth:tok123", c.SentLines());
        Assert.Contains("NICK viewer", c.SentLines());
        await c.In.Writer.WriteAsync("PING :tmi.twitch.tv\r\n@id=1 :a!a@a PRIVMSG #chan :hello\r\n");
        Assert.Equal("@id=1 :a!a@a PRIVMSG #chan :hello", await NextLine());
        Assert.Contains("PONG :tmi.twitch.tv", c.SentLines());
        irc.Stop();
    }

    [Fact]
    public async Task ReconnectCommandUsesTheCurrentToken()
    {
        login = "viewer"; token = "first";
        var irc = Client(); irc.Start("chan");
        var c1 = await NextConnection(1); await c1.Joined.Task;
        token = "refreshed";                                     // the hourly refresh replaced the token
        await c1.In.Writer.WriteAsync(":tmi.twitch.tv RECONNECT");
        var c2 = await NextConnection(2); await c2.Joined.Task;
        Assert.Contains("PASS oauth:refreshed", c2.SentLines());
        irc.Stop();
    }

    [Fact]
    public async Task ChatTextEndingInReconnectIsJustText()
    {
        var irc = Client(); irc.Start("chan");
        var c = await NextConnection(1); await c.Joined.Task;
        await c.In.Writer.WriteAsync("@id=2 :troll!t@t PRIVMSG #chan :type RECONNECT");
        await c.In.Writer.WriteAsync("@id=3 :a!a@a PRIVMSG #chan :marker");
        Assert.EndsWith("type RECONNECT", await NextLine());
        Assert.EndsWith("marker", await NextLine());
        lock (conns) Assert.Single(conns);
        irc.Stop();
    }

    [Fact]
    public async Task OversizedMessageReconnects()
    {
        var irc = Client(); irc.Start("chan");
        var c1 = await NextConnection(1); await c1.Joined.Task;
        await c1.In.Writer.WriteAsync(new TooLargeException(1024 * 1024));
        var c2 = await NextConnection(2); await c2.Joined.Task;
        irc.Stop();
    }

    [Fact]
    public async Task QuickDropsBackOffAndAHealthyConnectionResets()
    {
        var irc = Client(); irc.Start("chan");
        for (int i = 1; i <= 4; i++)
        {
            var c = await NextConnection(i); await c.Joined.Task;
            await c.In.Writer.WriteAsync((object?)null);          // server accepts, then closes at once
        }
        var c5 = await NextConnection(5); await c5.Joined.Task;
        now += TimeSpan.FromMinutes(5);                           // this one stays up for five minutes
        await c5.In.Writer.WriteAsync((object?)null);
        await NextConnection(6);
        irc.Stop();
        double[] w; lock (waits) w = waits.Select(x => x.TotalSeconds).ToArray();
        Assert.True(w.Length >= 5);
        double[] nominal = { 1, 2, 4, 8 };
        for (int i = 0; i < 4; i++) Assert.InRange(w[i], nominal[i] * 0.5, nominal[i]);
        Assert.InRange(w[4], 0.5, 1.0);                           // reset after the healthy connection
    }

    [Fact]
    public async Task StopEndsTheLoop()
    {
        var irc = Client(); irc.Start("chan");
        var c = await NextConnection(1); await c.Joined.Task;
        irc.Stop();
        await irc.Running!.WaitAsync(TimeSpan.FromSeconds(5));
        lock (conns) Assert.Single(conns);
        Assert.False(irc.Send("PRIVMSG #chan :hi"));
    }

    [Fact]
    public async Task SendIsOneLineOnly()
    {
        var irc = Client();
        Assert.False(irc.Send("PRIVMSG #chan :hi"));              // not connected
        irc.Start("chan");
        var c = await NextConnection(1); await c.Joined.Task;
        Assert.False(irc.Send("PRIVMSG #chan :a\r\nJOIN #other"));
        Assert.True(irc.Send("PRIVMSG #chan :hello"));
        for (int i = 0; i < 100 && !c.SentLines().Contains("PRIVMSG #chan :hello"); i++) await Task.Delay(10);
        Assert.Contains("PRIVMSG #chan :hello", c.SentLines());
        Assert.DoesNotContain(c.SentLines(), l => l.Contains("JOIN #other"));
        irc.Stop();
    }

    [Fact]
    public async Task BadChannelNeverConnects()
    {
        var irc = Client(); string? status = null;
        irc.Status += s => status = s;
        irc.Start("bad channel");
        await irc.Running!;
        Assert.Equal("Not a channel name.", status);
        lock (conns) Assert.Empty(conns);
    }
}

public class IrcLifecycleTests
{
    // A connection that only completes when the test says so: the old loop finishes connecting after Start was called again.
    sealed class SlowConn : IIrcTransport
    {
        public readonly TaskCompletionSource Connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<string?> never = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Aborted; volatile bool open;
        public bool IsOpen => open && !Aborted;
        public async Task ConnectAsync(Uri server, CancellationToken ct) { Connected.TrySetResult(); await Release.Task; open = true; }
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> ReceiveAsync(CancellationToken ct) { ct.Register(() => never.TrySetCanceled()); return never.Task; }
        public void Abort() { Aborted = true; never.TrySetResult(null); }
        public void Dispose() { }
    }

    [Fact]
    public async Task AnOldConnectionCannotReplaceTheNewOne()
    {
        var conns = new List<SlowConn>();
        var irc = new Irc((out string? l, out string? t) => { l = null; t = null; },
            () => { var c = new SlowConn(); lock (conns) conns.Add(c); return c; }, (d, ct) => Task.Delay(d, ct));
        irc.Start("first");
        SlowConn c1; while (true) { lock (conns) if (conns.Count == 1) { c1 = conns[0]; break; } await Task.Delay(5); }
        await c1.Connected.Task;                     // the first loop is inside ConnectAsync
        irc.Start("second");                         // switch channel
        SlowConn c2; while (true) { lock (conns) if (conns.Count == 2) { c2 = conns[1]; break; } await Task.Delay(5); }
        c2.Release.SetResult();                      // the new connection is up...
        for (int i = 0; i < 200 && !irc.Send("PRIVMSG #second :x"); i++) await Task.Delay(5);
        c1.Release.SetResult();                      // ...then the old one finishes connecting
        await Task.Delay(100);
        Assert.True(irc.Send("PRIVMSG #second :still mine"));
        irc.Stop();
        Assert.True(c2.Aborted);                     // Stop reaches the current connection
        Assert.False(irc.Send("PRIVMSG #second :after stop"));
    }
}

public class IrcLivenessTests
{
    [Fact]
    public async Task ASilentServerIsPingedThenDropped()
    {
        var conns = new List<FakeConn>();
        var irc = new Irc((out string? l, out string? t) => { l = null; t = null; },
            () => { var c = new FakeConn(); lock (conns) conns.Add(c); return c; }, (d, ct) => Task.CompletedTask)
        { IdleTimeout = TimeSpan.FromMilliseconds(150), PongTimeout = TimeSpan.FromMilliseconds(150) };
        irc.Start("chan");
        FakeConn c1; while (true) { lock (conns) if (conns.Count >= 1) { c1 = conns[0]; break; } await Task.Delay(5); }
        await c1.Joined.Task;
        // the server says nothing: after IdleTimeout we PING, after PongTimeout we reconnect
        for (int i = 0; i < 400; i++) { lock (conns) if (conns.Count >= 2) break; await Task.Delay(10); }
        Assert.Contains("PING :tmi.twitch.tv", c1.SentLines());
        lock (conns) Assert.True(conns.Count >= 2, "did not reconnect");
        irc.Stop();
    }

    [Fact]
    public async Task AnAnswerKeepsTheConnection()
    {
        var conns = new List<FakeConn>();
        var irc = new Irc((out string? l, out string? t) => { l = null; t = null; },
            () => { var c = new FakeConn(); lock (conns) conns.Add(c); return c; }, (d, ct) => Task.CompletedTask)
        { IdleTimeout = TimeSpan.FromMilliseconds(150), PongTimeout = TimeSpan.FromMilliseconds(400) };
        irc.Start("chan");
        FakeConn c1; while (true) { lock (conns) if (conns.Count >= 1) { c1 = conns[0]; break; } await Task.Delay(5); }
        await c1.Joined.Task;
        for (int i = 0; i < 200 && !c1.SentLines().Contains("PING :tmi.twitch.tv"); i++) await Task.Delay(5);
        await c1.In.Writer.WriteAsync(":tmi.twitch.tv PONG tmi.twitch.tv :tmi.twitch.tv");   // the server answers
        await Task.Delay(250);
        lock (conns) Assert.Single(conns);
        irc.Stop();
    }
}
