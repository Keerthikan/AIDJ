using System;
using System.Collections.Generic;
using System.Linq;

namespace AIDJ.Core.Services
{
    public class TransitionSpec
    {
        public double DurationSeconds { get; set; }
        public double StartOffsetA { get; set; }
        public double StartOffsetB { get; set; }
        public List<TransitionPoint> Points { get; set; } = new();
    }

    public class TransitionPoint
    {
        public double TimeSeconds { get; set; }
        public float VolumeA { get; set; }
        public float VolumeB { get; set; }
        public float FilterCutoffA { get; set; }
        public float FilterCutoffB { get; set; }
        public float TempoPercentB { get; set; }
    }

    public class TransitionContext
    {
        public TrackData Current { get; set; }
        public TrackData Next { get; set; }

        public float CurrentEnergy { get; set; }
        public float TargetEnergy { get; set; }

        public float Intensity { get; set; }
        public double PreferredDurationSeconds { get; set; }
    }

    public interface ITransitionPlanner
    {
        TransitionSpec Plan(TransitionContext context);
    }

    public class HeuristicTransitionPlanner : ITransitionPlanner
    {
        // Debug/inspektionsfelt: hvor mange beats blev brugt til groove-pattern matching i sidste planlægning
        public static int LastPatternBeats { get; private set; } = 0;

        // Ekstra debug-info om seneste planlagte transition
        public class DebugInfo
        {
            public double BaseFadeSeconds { get; set; }
            public double PreferredSeconds { get; set; }
            public double FinalDurationSeconds { get; set; }
            public bool HarmonicMerge { get; set; }
            public double StartOffsetA { get; set; }
            public double StartOffsetB { get; set; }
            public double EntryPosB { get; set; }
            public double WindowSeconds { get; set; }
            public double DesiredEnergyDelta { get; set; }
        }

        public static DebugInfo LastDebug { get; private set; }

        public TransitionSpec Plan(TransitionContext ctx)
        {
            var current = ctx.Current;
            var next = ctx.Next;

            double baseFade = TrackAnalysisService.CalculateIntuitiveFade(current, next) / 1000.0;
            double desired = ctx.PreferredDurationSeconds > 0
                ? ctx.PreferredDurationSeconds
                : baseFade;

            double duration = 0.5 * baseFade + 0.5 * desired;

            // Hvis tracks er harmonisk beslægtede, giver vi plads til et længere overlap ("merge")
            bool harmonicMerge = current.Key != null && next.Key != null &&
                                  TrackAnalysisService.IsHarmonicNeighbor(current.Key, next.Key);
            if (harmonicMerge)
            {
                duration *= 1.5; // længere overgang når harmonien er god
            }

            // Start lidt før MixOutPoint på A, så vi har en hale at arbejde med
            double startOffsetA = current.MixOutPoint - duration * 0.8;
            if (startOffsetA < 0) startOffsetA = 0;

            // For B vælger vi et indgangspunkt ved at matche spektret/energi mod halen af A
            double desiredEnergyDelta = ctx.TargetEnergy - ctx.CurrentEnergy;
            double windowSeconds = Math.Max(2.0, duration * 0.8);
            int patternBeatsUsed;
            double startOffsetB = FindBestEntryOffset(current, next, windowSeconds, desiredEnergyDelta, out patternBeatsUsed);
            LastPatternBeats = patternBeatsUsed;

            // Justér fade-længde efter hvor vi går ind i næste track
            double entryPos = 0.0;
            if (next.Duration.TotalSeconds > 0)
            {
                entryPos = startOffsetB / next.Duration.TotalSeconds; // 0 = start, 1 = slut

                if (!harmonicMerge)
                {
                    // Tidligt i sangen: længere, roligere fade
                    if (entryPos < 0.3) duration *= 1.2;
                    // Sent i sangen: kortere og mere beslutsom fade
                    else if (entryPos > 0.7) duration *= 0.8;
                }
                else
                {
                    // Harmonic merge: gå lidt ekstra roligt ind hvis vi er tidligt i B,
                    // og klip lidt hurtigere hvis vi er sent i B
                    if (entryPos < 0.3) duration *= 1.1;
                    else if (entryPos > 0.7) duration *= 0.9;
                }
            }

            var spec = new TransitionSpec
            {
                DurationSeconds = duration,
                StartOffsetA = startOffsetA,
                StartOffsetB = startOffsetB
            };

            // Gem debug-info til senere logging/inspektion
            LastDebug = new DebugInfo
            {
                BaseFadeSeconds = baseFade,
                PreferredSeconds = desired,
                FinalDurationSeconds = duration,
                HarmonicMerge = harmonicMerge,
                StartOffsetA = startOffsetA,
                StartOffsetB = startOffsetB,
                EntryPosB = entryPos,
                WindowSeconds = windowSeconds,
                DesiredEnergyDelta = desiredEnergyDelta
            };

            int steps = Math.Max(4, (int)(duration * 10));
            for (int i = 0; i <= steps; i++)
            {
                double t = i * (duration / steps);
                double progress = duration > 0 ? t / duration : 0.0; // 0..1

                float energyDelta = ctx.TargetEnergy - ctx.CurrentEnergy;
                double curvePower = energyDelta >= 0 ? 0.8 : 1.2;

                float volA;
                float volB;

                if (harmonicMerge)
                {
                    // Harmonic merge: længere periode hvor begge tracks er høje
                    if (progress < 0.3)
                    {
                        // Fade B ind mens A stadig dominerer
                        volA = 1.0f;
                        volB = (float)Math.Pow(progress / 0.3, 0.8);
                    }
                    else if (progress < 0.7)
                    {
                        // Midter-sektion: begge næsten fuldt oppe
                        double midPhase = (progress - 0.3) / 0.4; // 0..1
                        volA = (float)(1.0 - 0.2 * midPhase);      // 1.0 -> 0.8
                        volB = (float)(0.8 + 0.2 * midPhase);      // 0.8 -> 1.0
                    }
                    else
                    {
                        // Sidste del: fade A ud og lad B tage over
                        double endPhase = (progress - 0.7) / 0.3;    // 0..1
                        volA = (float)(0.8 * (1.0 - endPhase));     // 0.8 -> 0
                        volB = 1.0f;
                    }
                }
                else
                {
                    // Standard crossfade-kurve
                    volA = (float)Math.Pow(1 - progress, curvePower);
                    volB = (float)Math.Pow(progress, curvePower);
                }

                float cutoffA = 200f + (float)(progress * 4000f);
                float cutoffB = 8000f - (float)(progress * 6000f);

                float bpmRatio = current.Bpm > 0 && next.Bpm > 0
                    ? current.Bpm / next.Bpm
                    : 1f;
                float tempoB = 100f + (float)(progress * (bpmRatio * 100f - 100f));

                spec.Points.Add(new TransitionPoint
                {
                    TimeSeconds = t,
                    VolumeA = volA,
                    VolumeB = volB,
                    FilterCutoffA = cutoffA,
                    FilterCutoffB = cutoffB,
                    TempoPercentB = tempoB
                });
            }

            return spec;
        }

        // --- Evidence-based helpers til at vælge entry offset i næste track ---
        private static double FrameEnergy(float[] frame)
        {
            if (frame == null || frame.Length < 3) return 0;
            return frame[0] + frame[1] + frame[2];
        }

        private static double WindowAverageEnergy(List<float[]> map, double tStart, double tEnd)
        {
            var frames = map.Where(f => f[3] >= tStart && f[3] <= tEnd).ToList();
            if (frames.Count == 0) return 0;
            return frames.Average(FrameEnergy);
        }

        private static float[] WindowAverageSpectrum(List<float[]> map, double tStart, double tEnd)
        {
            var frames = map.Where(f => f[3] >= tStart && f[3] <= tEnd).ToList();
            if (frames.Count == 0) return new float[3];

            float bass = frames.Average(f => f[0]);
            float mids = frames.Average(f => f[1]);
            float highs = frames.Average(f => f[2]);
            return new[] { bass, mids, highs };
        }

        private static double SpectrumDistance(float[] a, float[] b)
        {
            double db0 = a[0] - b[0];
            double db1 = a[1] - b[1];
            double db2 = a[2] - b[2];
            return Math.Sqrt(db0 * db0 + db1 * db1 + db2 * db2);
        }

        // Byg en sekvens af beat-"fingeraftryk" (bass/mid/high) over et antal beats
        private static List<float[]> BuildBeatSequence(List<float[]> map, double bpm, double startTime, int beats)
        {
            var seq = new List<float[]>();
            if (map == null || map.Count == 0 || bpm <= 0 || beats <= 0)
                return seq;

            double beatLen = 60.0 / bpm;

            for (int b = 0; b < beats; b++)
            {
                double tStart = startTime + b * beatLen;
                double tEnd = tStart + beatLen;

                var frames = map.Where(f => f[3] >= tStart && f[3] <= tEnd).ToList();
                if (frames.Count == 0)
                    break;

                float bass = frames.Average(f => f[0]);
                float mids = frames.Average(f => f[1]);
                float highs = frames.Average(f => f[2]);
                seq.Add(new[] { bass, mids, highs });
            }

            return seq;
        }

        private static double SequenceDistance(List<float[]> seqA, List<float[]> seqB)
        {
            if (seqA == null || seqB == null || seqA.Count == 0 || seqB.Count == 0)
                return double.PositiveInfinity;

            int n = Math.Min(seqA.Count, seqB.Count);
            if (n == 0) return double.PositiveInfinity;

            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                var a = seqA[i];
                var b = seqB[i];
                double db0 = a[0] - b[0];
                double db1 = a[1] - b[1];
                double db2 = a[2] - b[2];
                sum += db0 * db0 + db1 * db1 + db2 * db2;
            }

            return Math.Sqrt(sum / n);
        }

        private static double FindBestEntryOffset(TrackData current, TrackData next,
                                                 double windowSeconds,
                                                 double desiredEnergyDelta,
                                                 out int beatsUsed)
        {
            var mapA = current.SpectralMap;
            var mapB = next.SpectralMap;
            beatsUsed = 0;
            if (mapA == null || mapB == null || mapA.Count == 0 || mapB.Count == 0)
            {
                return next.MixInPoint; // fallback til din eksisterende logik
            }

            // 1. Halen af A
            double aEnd = current.MixOutPoint;
            double aStart = Math.Max(0, aEnd - windowSeconds);
            var aAvgSpec = WindowAverageSpectrum(mapA, aStart, aEnd);
            double aEnergy = WindowAverageEnergy(mapA, aStart, aEnd);

            // Byg en adaptiv groove-sekvens i A: forsøg 16 beats, fald tilbage til 8 eller 4 hvis nødvendigt
            int beatsForPattern = 16;
            double beatLenA = current.Bpm > 0 ? 60.0 / current.Bpm : windowSeconds / beatsForPattern;
            double seqLenA = beatLenA * beatsForPattern;
            double seqStartA = Math.Max(0, aEnd - seqLenA);
            var seqA = BuildBeatSequence(mapA, current.Bpm, seqStartA, beatsForPattern);

            if (seqA.Count < 8)
            {
                beatsForPattern = 8;
                beatLenA = current.Bpm > 0 ? 60.0 / current.Bpm : windowSeconds / beatsForPattern;
                seqLenA = beatLenA * beatsForPattern;
                seqStartA = Math.Max(0, aEnd - seqLenA);
                seqA = BuildBeatSequence(mapA, current.Bpm, seqStartA, beatsForPattern);
            }

            if (seqA.Count < 4)
            {
                beatsForPattern = 4;
                beatLenA = current.Bpm > 0 ? 60.0 / current.Bpm : windowSeconds / beatsForPattern;
                seqLenA = beatLenA * beatsForPattern;
                seqStartA = Math.Max(0, aEnd - seqLenA);
                seqA = BuildBeatSequence(mapA, current.Bpm, seqStartA, beatsForPattern);
            }

            // 2. Kandidat-interval i B
            // Start et stykke inde i nummeret i stedet for helt ved MixInPoint,
            // så vi oftere rammer sektioner midt i tracket.
            double minStart = next.MixInPoint + 5.0; // 5 sekunder efter første "groove"
            double maxStart = next.Duration.TotalSeconds - windowSeconds - 2.0; // buffer til slutningen

            // Hvis tracket er for kort, falder vi tilbage til MixInPoint
            if (maxStart <= minStart)
                return next.MixInPoint;

            double bestOffset = minStart;
            double bestScore = double.NegativeInfinity;
            beatsUsed = (seqA != null && seqA.Count >= 2) ? beatsForPattern : 0;

            // 3. Scan kandidater i B på beat-grid, hvis muligt
            double beatLen = next.Bpm > 0 ? 60.0 / next.Bpm : 0.0;
            double step = beatLen > 0 ? beatLen : 0.25; // fallback til 0.25s hvis BPM ikke giver mening

            // Start på nærmeste beat efter minStart
            double t0 = minStart;
            if (beatLen > 0)
            {
                double beatsFromZero = Math.Ceiling(minStart / beatLen);
                t0 = beatsFromZero * beatLen;
                if (t0 < minStart) t0 += beatLen;
            }

            for (double t = t0; t <= maxStart; t += step)
            {
                double bStart = t;
                double bEnd = t + windowSeconds;

                var bAvgSpec = WindowAverageSpectrum(mapB, bStart, bEnd);
                double bEnergy = WindowAverageEnergy(mapB, bStart, bEnd);

                // Sekvens-baseret groove-match over flere beats i B
                double patternScore = 0.0;
                if (seqA != null && seqA.Count >= 2 && next.Bpm > 0)
                {
                    var seqB = BuildBeatSequence(mapB, next.Bpm, bStart, beatsForPattern);
                    if (seqB.Count >= 2)
                    {
                        double seqDist = SequenceDistance(seqA, seqB);
                        patternScore = -seqDist; // mindre afstand = bedre match
                    }
                }

                // Spektral kontinuitet (gennemsnitligt vindue)
                double specDist = SpectrumDistance(aAvgSpec, bAvgSpec);
                double continuityScore = -specDist; // mindre distance = bedre

                // Energi-match mod ønsket ændring
                double energyDelta = bEnergy - aEnergy;
                double energyScore = -Math.Abs(energyDelta - desiredEnergyDelta);

                // Straf for at starte for tæt på slutningen af sangen
                double tailPenalty = 0.0;
                double totalLen = next.Duration.TotalSeconds;
                if (totalLen > 0)
                {
                    double center = totalLen * 0.6; // vi vil typisk ligge før ~60% inde
                    if (t > center)
                    {
                        double tailFrac = (t - center) / (totalLen - center);
                        tailFrac = Math.Clamp(tailFrac, 0.0, 1.0);
                        tailPenalty = -tailFrac; // -1 når vi er helt til sidst
                    }
                }

                // Kombiner score: groove-pattern + spektral kontinuitet + energi + blød straf for at ligge tæt på slutningen
                double score = 0.5 * patternScore + 0.3 * continuityScore + 0.15 * energyScore + 0.05 * tailPenalty;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = t;
                }
            }

            return bestOffset;
        }
    }
}
