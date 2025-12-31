using System;
using AIDJ.Core.Services;

namespace AIDJ
{
    public static class ConsoleVisualizer
    {
        public static void HandleInput(DjEngine engine, double posSeconds)
        {
            if (engine == null) return;

            if (!Console.KeyAvailable) return;

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.J)
            {
                // Jump to 10 seconds before MixOutPoint on the current track
                engine.JumpToPreMixOut(10.0);
            }
            else if (key.Key == ConsoleKey.L)
            {
                // Log current transition context for later analysis
                TransitionLogger.Log(engine.CurrentTrack, engine.NextTrack, posSeconds);
            }
        }

        public static void Render(TrackData current, TrackData next, double pos, float[] bands)
        {
            // Compute derived values
            double introWindow = next != null ? TrackAnalysisService.CalculateIntroWindow(next) : 0;
            int fadeDuration = next != null ? TrackAnalysisService.CalculateIntuitiveFade(current, next) : 0;
            float compatibility = next != null ? TrackAnalysisService.CalculateCompatibility(current, next) : 0;
            bool isHarmonic = next != null && TrackAnalysisService.IsHarmonicNeighbor(current.Key, next.Key);

            // Draw VU meters (3-band view, similar to the original version)
            string bassBar = new string('█', Math.Min(25, (int)(bands[0] * 350))).PadRight(25);
            string midBar  = new string('█', Math.Min(25, (int)(bands[1] * 700))).PadRight(25);
            string highBar = new string('█', Math.Min(25, (int)(bands[2] * 1000))).PadRight(25);

            // Lock the cursor to the top of the interface to avoid flicker
            Console.SetCursorPosition(0, 8);

            // Clear a fixed number of lines under the cursor to avoid overlap
            for (int i = 0; i < 15; i++)
            {
                Console.WriteLine(new string(' ', Console.WindowWidth - 1));
            }
            Console.SetCursorPosition(0, 8);

            Console.WriteLine("================ AI DJ DEBUG MONITOR ================");
            Console.WriteLine($">> NOW:  {current.Title.PadRight(30).Substring(0, 30)} [{current.Key ?? "???"}] {current.Bpm:F1} BPM");
            Console.WriteLine($">> NEXT: {(next?.Title ?? "None").PadRight(29).Substring(0, 29)} [{next?.Key ?? "???"}] {(next?.Bpm ?? 0):F1} BPM");
            Console.WriteLine("-----------------------------------------------------");

            // Frequency monitor
            Console.ForegroundColor = ConsoleColor.Red;   Console.WriteLine($"BASS: [{bassBar}] {(bands[0] * 100):F0}%");
            Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"MIDS: [{midBar}] {(bands[1] * 100):F0}%");
            Console.ForegroundColor = ConsoleColor.Cyan;  Console.WriteLine($"HIGH: [{highBar}] {(bands[2] * 100):F0}%");
            Console.ResetColor();

            Console.WriteLine("-----------------------------------------------------");
            Console.WriteLine($"PROGRESS: {pos:F1}s / {current.MixOutPoint:F1}s (Mix Point)      ");
            Console.WriteLine($"INTRO WINDOW (NEXT): {introWindow:F1}s | FADE: {fadeDuration / 1000.0:F1}s      ");

            // Pattern info (how many beats were used for groove-matching)
            int patternBeats = HeuristicTransitionPlanner.LastPatternBeats;
            string patternText = patternBeats > 0 ? $"{patternBeats} beats" : "N/A";
            Console.WriteLine($"GROOVE PATTERN: {patternText}      ");

            // Confidence score
            Console.Write("MIX CONFIDENCE: ");
            if (next == null) { Console.WriteLine("N/A                  "); }
            else
            {
                if (compatibility > 0.8) Console.ForegroundColor = ConsoleColor.Green;
                else if (compatibility > 0.5) Console.ForegroundColor = ConsoleColor.Yellow;
                else Console.ForegroundColor = ConsoleColor.Red;

                Console.WriteLine($"{(compatibility * 100):F0}% ({(isHarmonic ? "Harmonic Match" : "BPM/Energy Only")})   ");
                Console.ResetColor();
            }
            Console.WriteLine("=====================================================");
        }
    }
}
