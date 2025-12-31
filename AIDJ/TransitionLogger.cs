using System;
using System.IO;
using AIDJ.Core.Services;

namespace AIDJ
{
    public static class TransitionLogger
    {
        private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "transition_log.txt");

        public static void Log(TrackData current, TrackData next, double posSeconds)
        {
            try
            {
                if (current == null && next == null)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath) ?? AppContext.BaseDirectory);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                string currentTitle = current?.Title ?? "<none>";
                string nextTitle = next?.Title ?? "<none>";

                float currentBpm = current?.Bpm ?? 0f;
                float nextBpm = next?.Bpm ?? 0f;

                string currentKey = current?.Key ?? "?";
                string nextKey = next?.Key ?? "?";

                string currentPath = current?.Path ?? "<none>";
                string nextPath = next?.Path ?? "<none>";

                string currentSpecPath = current != null ? current.Path + ".json" : "<none>";
                string nextSpecPath = next != null ? next.Path + ".json" : "<none>";

                // Mix-punkter
                double currentMixIn = current?.MixInPoint ?? 0.0;
                double currentMixOut = current?.MixOutPoint ?? 0.0;
                double nextMixIn = next?.MixInPoint ?? 0.0;
                double nextMixOut = next?.MixOutPoint ?? 0.0;

                // Heuristiske beslutninger
                float compatibility = (current != null && next != null)
                    ? TrackAnalysisService.CalculateCompatibility(current, next)
                    : 0f;

                bool harmonicNeighbor = (current != null && next != null)
                    && TrackAnalysisService.IsHarmonicNeighbor(current.Key, next.Key);

                double introWindow = (next != null)
                    ? TrackAnalysisService.CalculateIntroWindow(next)
                    : 0.0;

                int intuitiveFadeMs = (current != null && next != null)
                    ? TrackAnalysisService.CalculateIntuitiveFade(current, next)
                    : 0;

                var debug = HeuristicTransitionPlanner.LastDebug;
                int patternBeats = HeuristicTransitionPlanner.LastPatternBeats;

                string line = $"[{timestamp}] POS={posSeconds:F2}s | " +
                              $"CURRENT=\"{currentTitle}\" (BPM={currentBpm:F1}, Key={currentKey}, Path={currentPath}, MixIn={currentMixIn:F2}, MixOut={currentMixOut:F2}) | " +
                              $"NEXT=\"{nextTitle}\" (BPM={nextBpm:F1}, Key={nextKey}, Path={nextPath}, MixIn={nextMixIn:F2}, MixOut={nextMixOut:F2}) | " +
                              $"CurrentSpec={currentSpecPath} | NextSpec={nextSpecPath} | " +
                              $"Compat={compatibility:F3} HarmonicNeighbor={harmonicNeighbor} IntroWindowNext={introWindow:F2}s IntuitiveFade={intuitiveFadeMs/1000.0:F2}s | " +
                              $"PatternBeats={patternBeats}";

                if (debug != null)
                {
                    line += " | " +
                            $"Planner[BaseFade={debug.BaseFadeSeconds:F2}s, Preferred={debug.PreferredSeconds:F2}s, Final={debug.FinalDurationSeconds:F2}s, " +
                            $"HarmonicMerge={debug.HarmonicMerge}, StartA={debug.StartOffsetA:F2}s, StartB={debug.StartOffsetB:F2}s, EntryPosB={debug.EntryPosB:F2}, " +
                            $"Window={debug.WindowSeconds:F2}s, DesiredΔE={debug.DesiredEnergyDelta:F3}]";
                }

                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never crash playback – ignore all errors
            }
        }
    }
}
