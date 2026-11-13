// Twitch IRC over a WebSocket; reconnects by itself. Anonymous (read-only) without a token.
// Moved from src/chat.cs (where upstream had it) so reconnects, PING/PONG, RECONNECT, oversized messages,
// backoff and token changes can be tested against a fake server. The chat UI uses it exactly as before.
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LiteViewCore
{
    /// <summary>One connection to the chat server: whole text messages in and out.</summary>
    public interface IIrcTransport : IDisposable
    {
        Task ConnectAsync(Uri server, CancellationToken ct);
        Task SendAsync(string text, CancellationToken ct);
        /// <summary>One whole text message, or null when the server closed. Throws TooLargeException past the limit.</summary>
        Task<string?> ReceiveAsync(CancellationToken ct);
        bool IsOpen { get; }
        void Abort();
    }

    public sealed class WebSocketTransport : IIrcTransport
    {
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly FrameBuffer acc;
        readonly byte[] buf = new byte[32768];
        public WebSocketTransport(int maxMessageBytes = 1024 * 1024) { acc = new FrameBuffer(maxMessageBytes); }

        public Task ConnectAsync(Uri server, CancellationToken ct) { return ws.ConnectAsync(server, ct); }
        public bool IsOpen { get { return ws.State == WebSocketState.Open; } }
        public void Abort() { try { ws.Abort(); } catch (Exception) { } }

        public Task SendAsync(string text, CancellationToken ct)
        {
            var b = Encoding.UTF8.GetBytes(text);
            return ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, ct);
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                if (!acc.Append(buf, r.Count)) throw new TooLargeException(acc.Limit);
                if (r.EndOfMessage) return acc.Take();
            }
        }

        public void Dispose() { ws.Dispose(); acc.Dispose(); }
    }

    /// <summary>Gives the account to connect as: login and token from one moment (token null = anonymous).</summary>
    public delegate void SessionSource(out string? login, out string? token);

    public sealed class Irc : IDisposable
    {
        public static readonly Uri TwitchServer = new Uri("wss://irc-ws.chat.twitch.tv:443");
        public event Action<string>? Line;
        public event Action<string>? Status;
        public Uri Server = TwitchServer;
        /// <summary>Twitch sends PING about every 5 minutes. Quiet this long: we PING; no answer within PongTimeout: reconnect.
        /// (After sleep or a network change a dead socket can otherwise stay unnoticed for a long time.)</summary>
        public TimeSpan IdleTimeout = TimeSpan.FromMinutes(6), PongTimeout = TimeSpan.FromSeconds(20);

        readonly SessionSource session;
        readonly Func<IIrcTransport> newTransport;
        readonly Func<TimeSpan, CancellationToken, Task> delay;
        readonly Func<TimeSpan> clock;
        readonly Random rnd;
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1);
        // Start/Stop and publishing the current connection happen under `life`; `run` numbers the loops, so an
        // old loop that finishes connecting after a Stop/Start can never publish (or clear) the new connection.
        readonly object life = new object();
        volatile IIrcTransport? cur;
        CancellationTokenSource? cts;
        int run;

        /// <summary>The running connection loop (tests wait on it).</summary>
        public Task? Running { get; private set; }

        public Irc(SessionSource session, Func<IIrcTransport>? transport = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
            Func<TimeSpan>? clock = null, Random? random = null)
        {
            this.session = session;
            newTransport = transport ?? (() => new WebSocketTransport());
            this.delay = delay ?? ((t, ct) => Task.Delay(t, ct));
            var sw = Stopwatch.StartNew();
            this.clock = clock ?? (() => sw.Elapsed);
            rnd = random ?? new Random();
        }

        // The login and token are read again for every connection attempt, so a refreshed token is used.
        public void Start(string channel)
        {
            lock (life)
            {
                StopLocked();
                cts = new CancellationTokenSource();
                var c = cts.Token; int r = run;
                Running = Task.Run(() => Loop(channel, r, c));
            }
        }

        public void Stop() { lock (life) StopLocked(); }

        void StopLocked()
        {
            run++;
            if (cts != null) { cts.Cancel(); cts = null; }   // not disposed: the loop may still read its token
            var s = cur; cur = null;
            if (s != null) s.Abort();
        }

        public bool Send(string raw)
        {
            if (raw == null || raw.IndexOf('\r') >= 0 || raw.IndexOf('\n') >= 0) return false;   // one IRC line only
            var s = cur;
            if (s == null || !s.IsOpen) return false;
            _ = Task.Run(() => SendRaw(s, raw));
            return true;
        }

        async Task SendRaw(IIrcTransport s, string raw)
        {
            await sendGate.WaitAsync().ConfigureAwait(false);
            try { await s.SendAsync(raw + "\r\n", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { }
            finally { sendGate.Release(); }
        }

        void Say(string s) { Status?.Invoke(s); }

        async Task Loop(string chan, int r, CancellationToken c)
        {
            if (!IrcProtocol.IsSafeChannel(chan)) { Say("Not a channel name."); return; }
            // 1 s, 2 s, 4 s ... 30 s with jitter; reset only after a connection stayed up a minute
            var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), rnd);
            while (!c.IsCancellationRequested)
            {
                var s = newTransport(); TimeSpan upAt = TimeSpan.Zero; bool connected = false;
                Task<string?>? pending = null;
                try
                {
                    await s.ConnectAsync(Server, c).ConfigureAwait(false);
                    bool mine;
                    lock (life) { mine = r == run && !c.IsCancellationRequested; if (mine) cur = s; }
                    if (!mine) break;   // stopped or restarted while connecting: this connection is not used
                    connected = true; upAt = clock();
                    await SendRaw(s, "CAP REQ :twitch.tv/tags twitch.tv/commands").ConfigureAwait(false);
                    session(out var login, out var token);   // login and token from the same moment
                    int anon; lock (rnd) anon = rnd.Next(10000, 99999);
                    await SendRaw(s, token != null ? "PASS oauth:" + token : "PASS SCHMOOPIIE").ConfigureAwait(false);
                    await SendRaw(s, "NICK " + (token != null ? login : "justinfan" + anon)).ConfigureAwait(false);
                    await SendRaw(s, "JOIN #" + chan).ConfigureAwait(false);
                    bool again = false, pinged = false;
                    while (s.IsOpen && !again)
                    {
                        // one receive stays pending while the idle timer runs (cancelling a WebSocket receive would abort it)
                        pending ??= s.ReceiveAsync(c);   // one WebSocket message is never buffered past 1 MB
                        using (var t = CancellationTokenSource.CreateLinkedTokenSource(c))
                        {
                            var timer = Task.Delay(pinged ? PongTimeout : IdleTimeout, t.Token);
                            var first = await Task.WhenAny(pending, timer).ConfigureAwait(false);
                            t.Cancel();
                            if (first != pending)
                            {
                                if (c.IsCancellationRequested) break;
                                if (pinged) { Log.Info("chat: the server stopped answering, reconnecting"); break; }
                                pinged = true;
                                await SendRaw(s, "PING :tmi.twitch.tv").ConfigureAwait(false);
                                continue;
                            }
                        }
                        string? text = await pending.ConfigureAwait(false);
                        pending = null; pinged = false;
                        if (text == null) break;
                        foreach (var line in IrcProtocol.Lines(text))
                        {
                            string? pong = IrcProtocol.PongFor(line);
                            if (pong != null) { await SendRaw(s, pong).ConfigureAwait(false); continue; }
                            // only Twitch's RECONNECT command (upstream also matched chat messages ending in " RECONNECT")
                            if (line.IndexOf("RECONNECT", StringComparison.Ordinal) >= 0 && IrcProtocol.IsReconnect(IrcMsg.Parse(line))) { again = true; break; }
                            Line?.Invoke(line);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (TooLargeException) { Log.Warn("chat: oversized message from the server, reconnecting"); }
                catch (Exception ex) { if (!c.IsCancellationRequested) Log.Info("chat: connection ended: " + ex.GetType().Name); }
                finally
                {
                    lock (life) { if (cur == s) cur = null; }
                    try { s.Dispose(); } catch (Exception) { }
                    // a receive left pending fails once the socket is gone: observe it so it is not reported as unobserved
                    if (pending != null) _ = pending.ContinueWith(x => x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                if (c.IsCancellationRequested) break;
                if (connected) backoff.Ended(clock() - upAt);
                var wait = backoff.Next();
                Say("Reconnecting to chat...");
                try { await delay(wait, c).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }

        public void Dispose() { Stop(); sendGate.Dispose(); }
    }
}
