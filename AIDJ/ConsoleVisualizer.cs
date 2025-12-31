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
                // Jump til 10 sekunder før MixOutPoint på det nuværende track
                engine.JumpToPreMixOut(10.0);
            }
            else if (key.Key == ConsoleKey.L)
            {
                // Log nuværende transitionkontekst for senere analyse
                TransitionLogger.Log(engine.CurrentTrack, engine.NextTrack, posSeconds);
            }
        }

        public static void Render(TrackData current, TrackData next, double pos, float[] bands)
        {
            // Beregn værdier
            double introWindow = next != null ? TrackAnalysisService.CalculateIntroWindow(next) : 0;
            int fadeDuration = next != null ? TrackAnalysisService.CalculateIntuitiveFade(current, next) : 0;
            float compatibility = next != null ? TrackAnalysisService.CalculateCompatibility(current, next) : 0;
            bool isHarmonic = next != null && TrackAnalysisService.IsHarmonicNeighbor(current.Key, next.Key);

            // Tegn VU Meters (3-bånds visning, som i den oprindelige version)
            string bassBar = new string('█', Math.Min(25, (int)(bands[0] * 350))).PadRight(25);
            string midBar  = new string('█', Math.Min(25, (int)(bands[1] * 700))).PadRight(25);
            string highBar = new string('█', Math.Min(25, (int)(bands[2] * 1000))).PadRight(25);

            // Vi låser cursoren til toppen af interfacet for at undgå flimmer
            Console.SetCursorPosition(0, 8);

            // Ryd et fast antal linjer under cursoren for at undgå overlap
            for (int i = 0; i < 15; i++)
            {
                Console.WriteLine(new string(' ', Console.WindowWidth - 1));
            }
            Console.SetCursorPosition(0, 8);

            Console.WriteLine("================ AI DJ DEBUG MONITOR ================");
            Console.WriteLine($">> NOW:  {current.Title.PadRight(30).Substring(0, 30)} [{current.Key ?? "???"}] {current.Bpm:F1} BPM");
            Console.WriteLine($">> NEXT: {(next?.Title ?? "None").PadRight(29).Substring(0, 29)} [{next?.Key ?? "???"}] {(next?.Bpm ?? 0):F1} BPM");
            Console.WriteLine("-----------------------------------------------------");

            // Frekvens Monitor
            Console.ForegroundColor = ConsoleColor.Red;   Console.WriteLine($"BASS: [{bassBar}] {(bands[0] * 100):F0}%");
            Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"MIDS: [{midBar}] {(bands[1] * 100):F0}%");
            Console.ForegroundColor = ConsoleColor.Cyan;  Console.WriteLine($"HIGH: [{highBar}] {(bands[2] * 100):F0}%");
            Console.ResetColor();

            Console.WriteLine("-----------------------------------------------------");
            Console.WriteLine($"PROGRESS: {pos:F1}s / {current.MixOutPoint:F1}s (Mix Point)      ");
            Console.WriteLine($"INTRO WINDOW (NEXT): {introWindow:F1}s | FADE: {fadeDuration / 1000.0:F1}s      ");

            // Pattern info (hvor mange beats blev brugt til groove-match)
            int patternBeats = HeuristicTransitionPlanner.LastPatternBeats;
            string patternText = patternBeats > 0 ? $"{patternBeats} beats" : "N/A";
            Console.WriteLine($"GROOVE PATTERN: {patternText}      ");

            // Confidence Score
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
