using System.Net;
using System.Text;
using LiteViewCore;
using Xunit;

// A scripted Twitch: each request is answered by Route(request); tests can hold an answer back to create races.
sealed class FakeTwitch : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Route = _ => Task.FromResult(Json(404, "{}"));
    public readonly List<string> Calls = new List<string>();
    public readonly List<string> Bodies = new List<string>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage rq, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(rq.Method + " " + rq.RequestUri!.AbsolutePath);
        }
        if (rq.Content != null) { var b = await rq.Content.ReadAsStringAsync(ct); lock (Calls) Bodies.Add(b); }
        return await Route(rq);
    }

    public int Count(string pathEnd) { lock (Calls) return Calls.Count(c => c.EndsWith(pathEnd, StringComparison.Ordinal)); }

    public static HttpResponseMessage Json(int code, string body)
    {
        return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public const string Valid = "{\"client_id\":\"cid\",\"login\":\"viewer\",\"user_id\":\"1234\",\"scopes\":[\"chat:read\",\"user:read:emotes\"],\"expires_in\":5000}";
    public static string Tokens(string a, string r) { return "{\"access_token\":\"" + a + "\",\"refresh_token\":\"" + r + "\",\"expires_in\":14000}"; }
}

sealed class MemoryStore : ITokenStore
{
    public string? Token, Refresh; public int Saves, Deletes;
    public bool TryLoad(out string? token, out string? refresh) { token = Token; refresh = Refresh; return Token != null; }
    public void Save(string token, string? refresh) { Token = token; Refresh = refresh; Saves++; }
    public void Delete() { Token = Refresh = null; Deletes++; }
}

public class TwitchClientTests
{
    readonly FakeTwitch tw = new FakeTwitch();
    readonly MemoryStore store = new MemoryStore { Token = "old", Refresh = "r1" };
    readonly List<TimeSpan> waits = new List<TimeSpan>();
    TwitchClient Client(Func<DateTimeOffset>? now = null)
    {
        var c = new TwitchClient("cid", new HttpClient(tw), store, d => { lock (waits) waits.Add(d); return Task.CompletedTask; }, now);
        c.Load();
        return c;
    }

    static string Auth(HttpRequestMessage rq) { return rq.Headers.Authorization?.Parameter ?? ""; }

    [Fact]
    public async Task ValidTokenLogsIn()
    {
        tw.Route = rq => Task.FromResult(rq.RequestUri!.AbsolutePath.EndsWith("/validate") ? FakeTwitch.Json(200, FakeTwitch.Valid) : FakeTwitch.Json(404, "{}"));
        var c = Client();
        Assert.True(await c.Validate());
        Assert.Equal("viewer", c.Login);
        Assert.Equal("1234", c.UserId);
        Assert.True(c.Has("chat:read"));
        c.Session(out var login, out var token);
        Assert.Equal(("viewer", "old"), (login, token));
    }

    [Fact]
    public async Task ExpiredTokenIsRefreshedOnceAndSaved()
    {
        tw.Route = rq =>
        {
            string p = rq.RequestUri!.AbsolutePath;
            if (p.EndsWith("/validate")) return Task.FromResult(Auth(rq) == "new" ? FakeTwitch.Json(200, FakeTwitch.Valid) : FakeTwitch.Json(401, "{\"status\":401}"));
            if (p.EndsWith("/token")) return Task.FromResult(FakeTwitch.Json(200, FakeTwitch.Tokens("new", "r2")));
            return Task.FromResult(FakeTwitch.Json(404, "{}"));
        };
        var c = Client();
        Assert.True(await c.Validate());
        Assert.Equal("new", c.Token);
        Assert.Equal(("new", "r2"), (store.Token, store.Refresh));
        Assert.Equal(1, tw.Count("/token"));
        Assert.Contains("refresh_token=r1", tw.Bodies.Single(b => b.Contains("grant_type=refresh_token")));
    }

    [Fact]
    public async Task RejectedRefreshLogsOutAndDeletesTheFile()
    {
        tw.Route = rq => Task.FromResult(rq.RequestUri!.AbsolutePath.EndsWith("/validate") ? FakeTwitch.Json(401, "{}") : FakeTwitch.Json(400, "{\"message\":\"Invalid refresh token\"}"));
        var c = Client();
        Assert.False(await c.Validate());
        Assert.Null(c.Token); Assert.Null(c.Login);
        Assert.Null(store.Token);
        Assert.Equal(1, store.Deletes);
    }

    [Theory]
    [InlineData(0)]     // network error
    [InlineData(503)]
    [InlineData(429)]
    public async Task UnreachableRefreshKeepsTheLogin(int code)
    {
        // upstream deleted the login when Twitch merely did not answer
        tw.Route = rq =>
        {
            if (rq.RequestUri!.AbsolutePath.EndsWith("/validate")) return Task.FromResult(FakeTwitch.Json(401, "{}"));
            if (code == 0) throw new HttpRequestException("no route to host");
            return Task.FromResult(FakeTwitch.Json(code, "{}"));
        };
        var c = Client();
        Assert.False(await c.Validate());
        Assert.Equal("old", c.Token);          // kept for the next try
        Assert.Null(c.Login);                  // but not a validated session: chat anonymous, sending off
        c.Session(out var login, out var token);
        Assert.Null(login); Assert.Null(token);
        Assert.Equal(("old", "r1"), (store.Token, store.Refresh));
        Assert.Equal(0, store.Deletes);
    }

    [Fact]
    public async Task LogOutDuringARefreshIsNotUndone()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        var refreshStarted = new TaskCompletionSource();
        tw.Route = rq =>
        {
            string p = rq.RequestUri!.AbsolutePath;
            if (p.EndsWith("/validate")) return Task.FromResult(FakeTwitch.Json(401, "{}"));
            if (p.EndsWith("/token")) { refreshStarted.TrySetResult(); return release.Task; }
            return Task.FromResult(FakeTwitch.Json(200, "{}"));   // revoke
        };
        var c = Client();
        var validating = c.Validate();
        await refreshStarted.Task;
        c.LogOut();
        release.SetResult(FakeTwitch.Json(200, FakeTwitch.Tokens("resurrected", "r9")));
        Assert.False(await validating);
        Assert.Null(c.Token);
        Assert.Null(store.Token);              // the refresh answer did not write the token back
        Assert.Equal(1, tw.Count("/revoke"));
    }

    [Fact]
    public async Task StaleValidationDoesNotOverwriteANewLogin()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        int validates = 0;
        tw.Route = rq =>
        {
            string p = rq.RequestUri!.AbsolutePath;
            if (p.EndsWith("/validate"))
            {
                if (Interlocked.Increment(ref validates) == 1) return release.Task;   // the old token's validation hangs
                return Task.FromResult(FakeTwitch.Json(200, FakeTwitch.Valid.Replace("viewer", "second").Replace("1234", "5678")));
            }
            if (p.EndsWith("/token")) return Task.FromResult(FakeTwitch.Json(200, FakeTwitch.Tokens("fresh", "r5")));   // device login
            return Task.FromResult(FakeTwitch.Json(404, "{}"));
        };
        var c = Client();
        var stale = c.Validate();
        Assert.Equal("ok", await c.PollDevice("dev"));           // a new login completes meanwhile
        release.SetResult(FakeTwitch.Json(200, FakeTwitch.Valid)); // then the old answer arrives
        await stale;
        Assert.Equal("second", c.Login);
        Assert.Equal("fresh", c.Token);
    }

    [Fact]
    public async Task ConcurrentExpiredRequestsRefreshOnlyOnce()
    {
        // one-time refresh tokens: a second refresh with r1 would fail and log the user out
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        tw.Route = rq =>
        {
            string p = rq.RequestUri!.AbsolutePath;
            if (p.EndsWith("/token")) return gate.Task;
            if (p.StartsWith("/helix/")) return Task.FromResult(Auth(rq) == "new" ? FakeTwitch.Json(200, "{\"data\":[1]}") : FakeTwitch.Json(401, "{}"));
            return Task.FromResult(FakeTwitch.Json(404, "{}"));
        };
        var c = Client();
        var a = c.Helix("users"); var b = c.Helix("streams/followed?user_id=1");
        while (tw.Count("/token") == 0) await Task.Delay(5);
        await Task.Delay(50);
        gate.SetResult(FakeTwitch.Json(200, FakeTwitch.Tokens("new", "r2")));
        Assert.NotNull(await a); Assert.NotNull(await b);
        Assert.Equal(1, tw.Count("/token"));
        Assert.Equal("new", c.Token);
    }

    [Fact]
    public async Task TokenFromAnotherAppIsRejected()
    {
        tw.Route = rq => Task.FromResult(rq.RequestUri!.AbsolutePath.EndsWith("/validate") ? FakeTwitch.Json(200, FakeTwitch.Valid.Replace("\"cid\"", "\"other\"")) : FakeTwitch.Json(200, "{}"));
        var c = Client();
        Assert.False(await c.Validate());
        Assert.Null(c.Token); Assert.Null(store.Token);
    }

    [Fact]
    public async Task OfflineValidationKeepsTheSession()
    {
        int n = 0;
        tw.Route = rq =>
        {
            if (Interlocked.Increment(ref n) == 1) return Task.FromResult(FakeTwitch.Json(200, FakeTwitch.Valid));
            throw new HttpRequestException("offline");
        };
        var c = Client();
        Assert.True(await c.Validate());
        Assert.True(await c.Validate());       // Twitch unreachable: still logged in
        Assert.Equal("viewer", c.Login);
    }

    [Fact]
    public async Task RateLimitWaitsForTheResetOnce()
    {
        var t0 = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        int n = 0;
        tw.Route = rq =>
        {
            if (Interlocked.Increment(ref n) == 1)
            {
                var r = FakeTwitch.Json(429, "{}");
                r.Headers.Add("Ratelimit-Reset", (t0.ToUnixTimeSeconds() + 4).ToString());
                return Task.FromResult(r);
            }
            return Task.FromResult(FakeTwitch.Json(200, "{\"data\":[]}"));
        };
        var c = Client(() => t0);
        Assert.NotNull(await c.Helix("users"));
        Assert.Equal(new[] { TimeSpan.FromSeconds(4) }, waits);
    }

    [Fact]
    public async Task OversizedAnswersAreRefused()
    {
        tw.Route = rq => Task.FromResult(FakeTwitch.Json(200, "{\"data\":\"" + new string('x', 5000) + "\"}"));
        var c = Client(); c.MaxJsonBytes = 1000;
        Assert.Null(await c.Helix("users"));
        Assert.Null(await c.Get("https://7tv.io/v3/emote-sets/global"));
    }

    [Fact]
    public async Task UnknownApiHostsAreNotContacted()
    {
        var c = Client();
        Assert.Null(await c.Get("https://evil.example/emotes"));
        Assert.Empty(tw.Calls);
    }

    [Fact]
    public async Task StalledDownloadEndsAtTheDeadline()
    {
        tw.Route = rq =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
            return Task.FromResult(r);
        };
        var c = Client(); c.DownloadTimeout = TimeSpan.FromMilliseconds(300);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await c.GetBytes("https://cdn.7tv.app/emote/x/1x.gif", 1000));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "took " + sw.Elapsed);
    }

    [Fact]
    public async Task DeviceFlowStates()
    {
        int polls = 0;
        tw.Route = rq =>
        {
            string p = rq.RequestUri!.AbsolutePath;
            if (p.EndsWith("/device")) return Task.FromResult(FakeTwitch.Json(200, "{\"device_code\":\"dc\",\"user_code\":\"ABCD1234\",\"verification_uri\":\"https://www.twitch.tv/activate?device-code=ABCD1234\",\"interval\":5,\"expires_in\":1800}"));
            if (p.EndsWith("/token"))
            {
                int k = Interlocked.Increment(ref polls);
                if (k == 1) return Task.FromResult(FakeTwitch.Json(400, "{\"status\":400,\"message\":\"authorization_pending\"}"));
                if (k == 2) throw new HttpRequestException("blip");
                return Task.FromResult(FakeTwitch.Json(200, FakeTwitch.Tokens("dev-token", "dev-refresh")));
            }
            if (p.EndsWith("/validate")) return Task.FromResult(FakeTwitch.Json(200, FakeTwitch.Valid));
            return Task.FromResult(FakeTwitch.Json(404, "{}"));
        };
        store.Token = store.Refresh = null;
        var c = Client();
        var d = await c.StartDevice();
        Assert.Equal("ABCD1234", Json.S(d, "user_code"));
        Assert.Equal("authorization_pending", await c.PollDevice("dc"));
        Assert.Equal("authorization_pending", await c.PollDevice("dc"));   // network blip: keep polling
        Assert.Equal("ok", await c.PollDevice("dc"));
        Assert.Equal(("dev-token", "dev-refresh"), (store.Token, store.Refresh));
        Assert.Contains(tw.Bodies, b => b.Contains("scopes=user%3Aread%3Afollows%20user%3Aread%3Aemotes%20chat%3Aread%20chat%3Aedit"));
    }

    [Fact]
    public async Task LogOutRevokesAndForgets()
    {
        tw.Route = rq => Task.FromResult(rq.RequestUri!.AbsolutePath.EndsWith("/validate") ? FakeTwitch.Json(200, FakeTwitch.Valid) : FakeTwitch.Json(200, "{}"));
        var c = Client();
        await c.Validate();
        c.LogOut();
        Assert.Null(c.Token); Assert.Null(c.Login); Assert.Empty(c.Granted);
        Assert.Null(store.Token);
        for (int i = 0; i < 100 && tw.Count("/revoke") == 0; i++) await Task.Delay(10);
        Assert.Contains(tw.Bodies, b => b.Contains("token=old"));
    }

    sealed class StallingStream : Stream
    {
        readonly TaskCompletionSource<int> never = new TaskCompletionSource<int>();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            using (ct.Register(() => never.TrySetCanceled())) return await never.Task;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { return new ValueTask<int>(ReadAsync(new byte[1], 0, 1, ct)); }
        protected override void Dispose(bool disposing) { never.TrySetException(new ObjectDisposedException("stream")); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) { return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult(); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
    }
}
