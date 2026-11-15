// Screens and graphics cards: where the window goes back to after fullscreen when monitors changed meanwhile
// (UI-2), and whether the RTX Video option can work at all (EXT-6).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteViewCore
{
    /// <summary>A rectangle in screen pixels (System.Drawing is not referenced by the core library).</summary>
    public readonly struct Box : IEquatable<Box>
    {
        public readonly int X, Y, W, H;
        public Box(int x, int y, int w, int h) { X = x; Y = y; W = Math.Max(0, w); H = Math.Max(0, h); }
        public int Right { get { return X + W; } }
        public int Bottom { get { return Y + H; } }
        public long Area { get { return (long)W * H; } }

        public long Overlap(Box o)
        {
            long w = Math.Min(Right, o.Right) - Math.Max(X, o.X), h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        public bool Equals(Box o) { return X == o.X && Y == o.Y && W == o.W && H == o.H; }
        public override bool Equals(object? obj) { return obj is Box b && Equals(b); }
        public static bool operator ==(Box a, Box b) { return a.Equals(b); }
        public static bool operator !=(Box a, Box b) { return !a.Equals(b); }
        public override int GetHashCode() { return X ^ Y << 8 ^ W << 16 ^ H << 24; }
        public override string ToString() { return X + "," + Y + " " + W + "x" + H; }
    }

    public static class Display
    {
        /// <summary>
        /// RTX Video (super resolution) needs an NVIDIA RTX card: GeForce RTX, (Quadro) RTX or RTX A/Ada.
        /// "NVIDIA driver installed" alone is also true for GTX cards, where the option does nothing.
        /// </summary>
        public static bool HasRtx(IEnumerable<string?> adapterNames)
        {
            return adapterNames.Any(n => n != null && n.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0
                && Regex.IsMatch(n, @"(^|[^A-Za-z])RTX([^A-Za-z]|$)", RegexOptions.IgnoreCase));
        }

        /// <summary>
        /// The window's place after fullscreen: where it was, if at least a quarter of it is still on a screen;
        /// otherwise (that monitor is gone or moved) centred on <paramref name="current"/>, no larger than it.
        /// </summary>
        public static Box Restore(Box saved, IList<Box> workAreas, Box current)
        {
            long onScreen = workAreas.Sum(a => a.Overlap(saved));
            if (saved.Area > 0 && onScreen * 4 >= saved.Area) return saved;
            int w = Math.Min(Math.Max(saved.W, 640), current.W), h = Math.Min(Math.Max(saved.H, 400), current.H);
            return new Box(current.X + (current.W - w) / 2, current.Y + (current.H - h) / 2, w, h);
        }
    }
}
