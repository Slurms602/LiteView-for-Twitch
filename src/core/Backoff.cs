// Exponential backoff with jitter, for chat reconnects and stream restarts: quick first retries,
// then slower, never a tight restart loop. The attempt count resets only after a connection
// stayed up for a while, so a server that accepts and drops at once is still slowed down.
using System;

namespace LiteViewCore
{
    public sealed class Backoff
    {
        readonly Random rnd;
        public TimeSpan First { get; }
        public TimeSpan Max { get; }
        /// <summary>A connection that lasted this long counts as healthy and resets the delay.</summary>
        public TimeSpan Healthy { get; }
        public int Attempt { get; private set; }

        public Backoff(TimeSpan first, TimeSpan max, TimeSpan healthy, Random? random = null)
        {
            if (first <= TimeSpan.Zero || max < first) throw new ArgumentOutOfRangeException(nameof(first));
            First = first; Max = max; Healthy = healthy; rnd = random ?? new Random();
        }

        /// <summary>The wait before the next attempt: First * 2^n capped at Max, then 50-100% of that (jitter).</summary>
        public TimeSpan Next()
        {
            double ms = First.TotalMilliseconds * Math.Pow(2, Math.Min(Attempt, 30));
            ms = Math.Min(ms, Max.TotalMilliseconds);
            Attempt++;
            double j;
            lock (rnd) j = 0.5 + rnd.NextDouble() * 0.5;
            return TimeSpan.FromMilliseconds(Math.Max(1, ms * j));
        }

        /// <summary>Call when a connection ends; it resets the delay if it had been up long enough.</summary>
        public void Ended(TimeSpan upFor) { if (upFor >= Healthy) Attempt = 0; }

        public void Reset() { Attempt = 0; }
    }
}
