// The picture presets' shader lists (P3 "preset review"). Each preset sharpens once at most, on the final picture,
// with the strength the user chose (Sharpness, for High and Ultra; Enhance keeps FSR's own + CAS):
//   Enhance: AMD FSR 1 upscale + AMD CAS at 1:1                          (light, any graphics card)
//   High:    FSRCNNX (8) upscale + Adaptive Sharpen                       (more GPU)
//   Ultra:   KrigBilateral colour + NLMeans noise clean-up + FSRCNNX (16) + SSimDownscaler + Adaptive Sharpen (most GPU)
// Ultra used to stack NLMeans' sharpening variant and the strong Adaptive Sharpen: two sharpeners, oversharpened
// (the owner's report). Its NLMeans is the denoise-only profile now, and its sharpening the same as High's.
using System.Collections.Generic;

namespace LiteViewCore
{
    public enum Sharpness { Off, Soft, Normal, Strong }

    public static class PicturePreset
    {
        public static readonly string[] SharpnessNames = { "Off", "Soft", "Normal (default)", "Strong" };

        /// <summary>RTX Video Super Resolution only when the video is enlarged at least this much: on the owner's PC it made a
        /// 1080p stream softer than Enhance alone at 1.05x and again at 1.23x (their usual window), so it starts at 1.3x
        /// (1080p in fullscreen on a 1440p screen is 1.33x; a 720p stream is about 2x).</summary>
        public const double RtxMinScale = 1.3;

        /// <summary>d3d11vpp's scale for RTX ("1.33"), or null: RTX waits (the video is shown at about its own size).</summary>
        public static string? RtxScale(double scale)
        {
            if (double.IsNaN(scale) || scale < RtxMinScale) return null;
            return System.Math.Min(scale, 4.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The preset's name (0 Off, 1 Enhance, 2 High, 3 Ultra), as in the picture box.</summary>
        public static string Name(int picture)
        {
            switch (picture) { case 1: return "Enhance"; case 2: return "High"; case 3: return "Ultra"; default: return "Off"; }
        }

        /// <summary>The shader files (in config/mpv/shaders) for a preset: 0 Off, 1 Enhance, 2 High, 3 Ultra.</summary>
        public static List<string> Shaders(int picture, Sharpness sharp)
        {
            var list = new List<string>();
            switch (picture)
            {
                case 1: list.Add("FSR.glsl"); list.Add("CAS.glsl"); break;
                case 2: list.Add("FSRCNNX_x2_8-0-4-1_any.glsl"); break;
                case 3: list.Add("KrigBilateral.glsl"); list.Add("nlmeans.glsl"); list.Add("FSRCNNX_x2_16-0-4-1_any.glsl"); list.Add("SSimDownscaler.glsl"); break;
                default: return list;
            }
            string? s = picture >= 2 ? Sharpener(sharp) : null;
            if (s != null) list.Add(s);
            return list;
        }

        public static string? Sharpener(Sharpness s)
        {
            switch (s)
            {
                case Sharpness.Off: return null;
                case Sharpness.Soft: return "adaptive-sharpen_soft.glsl";
                case Sharpness.Strong: return "adaptive-sharpen_strong.glsl";
                default: return "adaptive-sharpen.glsl";
            }
        }

        /// <summary>The shaders in a list that sharpen (each preset has one at most).</summary>
        public static int Sharpeners(IEnumerable<string> shaders)
        {
            int n = 0;
            foreach (var s in shaders) if (s.Contains("sharpen") || s == "CAS.glsl") n++;
            return n;
        }

        public static Sharpness ParseSharpness(string? s)
        {
            switch (s) { case "off": return Sharpness.Off; case "soft": return Sharpness.Soft; case "strong": return Sharpness.Strong; default: return Sharpness.Normal; }
        }

        public static string Save(Sharpness s) { return s.ToString().ToLowerInvariant(); }
    }
}
