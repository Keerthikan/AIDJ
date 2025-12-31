using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManagedBass;
using ManagedBass.Fx;

namespace AIDJ.Core.Services
{
    public class TrackAnalysisService
    {
        public TrackAnalysisService() { }

        public TrackData AnalyzeTrack(string filePath)
        {
            string mapPath = filePath + ".json";

            // 1. Hvis JSON ikke findes (eller er gammelt format), kør fuld analyse
            if (!File.Exists(mapPath))
            {
                GenerateFullAnalysis(filePath);
            }

            // 2. Læs den samlede analyse fra JSON
            string json = File.ReadAllText(mapPath);
            AnalysisContainer container;
            try
            {
                container = JsonSerializer.Deserialize<AnalysisContainer>(json);
            }
            catch
            {
                // Hvis JSON er i det gamle format (kun en liste), sletter vi og genanalyserer
                GenerateFullAnalysis(filePath);
                container = JsonSerializer.Deserialize<AnalysisContainer>(File.ReadAllText(mapPath));
            }

            // 3. Metadata Analysis via TagLib
            var tfile = TagLib.File.Create(filePath);
            var title = tfile.Tag.Title ?? Path.GetFileName(filePath);

            // Simple energy measurement: average sum of bass/mid/high over the whole track
            float energy = 0f;
            if (container.Map != null && container.Map.Count > 0)
            {
                energy = container.Map.Average(f => f[0] + f[1] + f[2]);
                if (energy < 0f) energy = 0f;
                if (energy > 1f) energy = 1f; // clamp to [0,1] for scoring
            }

            double mixIn = FindMixInPointBeatAware(container.Map, container.Bpm);
            double mixOut = FindMixOutPointBeatAware(container.Map, container.Bpm, tfile.Properties.Duration.TotalSeconds);

            return new TrackData
            {
                Title = title,
                Bpm = container.Bpm,
                Key = container.Key,
                Path = filePath,
                Duration = tfile.Properties.Duration,
                Energy = energy,
                SpectralMap = container.Map,
                MixInPoint = mixIn,
                MixOutPoint = mixOut
            };
        }

        private void GenerateFullAnalysis(string filePath)
        {
            string mapPath = filePath + ".json";

            // Brug Decode flag for lynhurtig analyse
            int decodeStream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode);
            if (decodeStream == 0) return;

            // Find BPM med det samme
            float bpm = (float)BassFx.BPMDecodeGet(decodeStream, 0, 15, 0, 0, null, IntPtr.Zero);

            List<float[]> spectralMap = new();
            float[] chroma = new float[12];
            float[] fft = new float[1024]; // Svarer til FFT2048

            double snapshotInterval = 0.1;
            double nextSnapshotTime = 0.0;
            double sampleRate = 44100.0; // Standard

            while (Bass.ChannelIsActive(decodeStream) == PlaybackState.Playing)
            {
                long bytePos = Bass.ChannelGetPosition(decodeStream);
                double currentSeconds = Bass.ChannelBytes2Seconds(decodeStream, bytePos);

                // Træk FFT data
                if (Bass.ChannelGetData(decodeStream, fft, (int)DataFlags.FFT2048) < 0) break;

                // --- KROMA ANALYSE (Til Key Detection) ---
                for (int i = 0; i < fft.Length / 2; i++)
                {
                    float freq = (float)(i * sampleRate / 2048.0);
                    if (freq < 50 || freq > 4000) continue; // Fokus på musikalsk område

                    // Hz til Note Index (0-11)
                    double note = 12 * Math.Log2(freq / 440.0) + 69;
                    int noteIndex = (int)Math.Round(note) % 12;
                    if (noteIndex >= 0) chroma[noteIndex] += fft[i];
                }

                // --- SPEKTRAL MAP (Til Visualizer & Mix Points) ---
                if (currentSeconds >= nextSnapshotTime)
                {
                    // Til visualisering og mix-punkter bruger vi 3 bånd + tid
                    float[] dataPoint = new float[4];
                    dataPoint[0] = fft.Take(10).Average();           // Bass
                    dataPoint[1] = fft.Skip(10).Take(100).Average(); // Mids
                    dataPoint[2] = fft.Skip(110).Average();          // Highs
                    dataPoint[3] = (float)currentSeconds;            // Tid i sekunder

                    spectralMap.Add(dataPoint);
                    nextSnapshotTime += snapshotInterval;
                }
            }

            // Map kromagram til Camelot Key
            string detectedKey = MapChromaToCamelot(chroma);

            // Gem alt i containeren
            var container = new AnalysisContainer
            {
                Key = detectedKey,
                Bpm = bpm,
                Map = spectralMap
            };

            var options = new JsonSerializerOptions { WriteIndented = false };
            File.WriteAllText(mapPath, JsonSerializer.Serialize(container, options));

            Bass.StreamFree(decodeStream);
        }

        private static string MapChromaToCamelot(float[] chroma)
        {
            int rootNote = Array.IndexOf(chroma, chroma.Max());

            // Tjek minor vs major terts
            float minorThird = chroma[(rootNote + 3) % 12];
            float majorThird = chroma[(rootNote + 4) % 12];
            bool isMinor = minorThird > majorThird;

            // Camelot Wheel Mapping (0=C, 1=C#, 2=D...)
            string[] camelotMinor = { "5A", "12A", "7A", "2A", "9A", "4A", "11A", "6A", "1A", "8A", "3A", "10A" };
            string[] camelotMajor = { "8B", "3B", "10B", "5B", "12B", "7B", "2B", "9B", "4B", "11B", "6B", "1B" };

            return isMinor ? camelotMinor[rootNote] : camelotMajor[rootNote];
        }

        /// <summary>
        /// Compute a simple per-beat energy curve from the spectral map and BPM.
        /// Returns a list of (beatIndex, timeSeconds, energy).
        /// </summary>
        private static List<(int BeatIndex, double Time, double Energy)> BuildBeatEnergyCurve(List<float[]> spectralMap, float bpm)
        {
            var result = new List<(int, double, double)>();
            if (spectralMap == null || spectralMap.Count == 0 || bpm <= 0)
                return result;

            int timeIndex = spectralMap[0].Length - 1;
            double beatLen = 60.0 / bpm;

            double trackSeconds = spectralMap.Last()[timeIndex];
            int totalBeats = Math.Max(1, (int)Math.Ceiling(trackSeconds / beatLen));

            int frameIndex = 0;
            for (int beat = 0; beat < totalBeats; beat++)
            {
                double start = beat * beatLen;
                double end = start + beatLen;

                double sumEnergy = 0.0;
                int count = 0;

                // accumulate frames within this beat window
                while (frameIndex < spectralMap.Count && spectralMap[frameIndex][timeIndex] < start)
                    frameIndex++;

                int j = frameIndex;
                while (j < spectralMap.Count && spectralMap[j][timeIndex] < end)
                {
                    var frame = spectralMap[j];
                    double e = frame[0] + frame[1] + frame[2];
                    sumEnergy += e;
                    count++;
                    j++;
                }

                if (count > 0)
                {
                    double avgEnergy = sumEnergy / count;
                    double midTime = start + beatLen * 0.5;
                    result.Add((beat, midTime, avgEnergy));
                }
            }

            return result;
        }

        /// <summary>
        /// Beat-aware mix-in point using a hybrid of derivative and plateau logic.
        /// 1) Build per-beat energy, 2) find strong positive changes, 3) validate
        /// that they lead into a reasonably stable, high-energy plateau.
        /// </summary>
        public static double FindMixInPointBeatAware(List<float[]> spectralMap, float bpm)
        {
            var beats = BuildBeatEnergyCurve(spectralMap, bpm);
            if (beats.Count == 0)
                return 0.0;

            // Compute robust statistics on the first half of the track
            int half = beats.Count / 2;
            var firstHalf = beats.Take(Math.Max(half, 1)).ToList();
            var firstHalfE = firstHalf.Select(b => b.Energy).OrderBy(x => x).ToList();
            double median = firstHalfE[firstHalfE.Count / 2];

            // Build a smoothed energy curve over beats
            int smoothWindow = 3; // beats
            double[] smoothE = new double[beats.Count];
            for (int i = 0; i < beats.Count; i++)
            {
                int start = Math.Max(0, i - smoothWindow);
                int end = Math.Min(beats.Count - 1, i + smoothWindow);
                int len = end - start + 1;
                double sum = 0.0;
                for (int j = start; j <= end; j++)
                    sum += beats[j].Energy;
                smoothE[i] = sum / len;
            }

            // Derivative of smoothed energy
            double[] dE = new double[beats.Count];
            dE[0] = 0.0;
            for (int i = 1; i < beats.Count; i++)
                dE[i] = smoothE[i] - smoothE[i - 1];

            // Use a relative threshold on positive changes to find candidate "drop in" points
            double absMaxPos = dE.Max(v => Math.Max(0.0, v));
            double riseThresh = absMaxPos * 0.5; // top ~50% of positive changes
            double highLevel = median * 1.3;      // plateau should be clearly above intro median

            int plateauBeats = 8; // ~2 bars

            for (int i = 1; i < beats.Count - plateauBeats; i++)
            {
                // Skip the very beginning to allow clean intros
                if (beats[i].Time < 2.0)
                    continue;

                bool strongRise = dE[i] >= riseThresh;
                if (!strongRise)
                    continue;

                // Validate plateau starting at this beat
                var window = beats.Skip(i).Take(plateauBeats).ToList();
                double minE = window.Min(b => b.Energy);
                double maxE = window.Max(b => b.Energy);

                if (minE > highLevel)
                {
                    // plateau should be relatively stable (no explosive ramp-up inside plateau)
                    if (maxE < minE * 1.3)
                        return beats[i].Time;
                }
            }

            // fallback: first beat time
            return beats[0].Time;
        }

        /// <summary>
        /// Beat-aware mix-out point using derivative + plateau:
        /// find the last stable high-energy plateau that is followed by
        /// a sustained drop in energy.
        /// </summary>
        public static double FindMixOutPointBeatAware(List<float[]> spectralMap, float bpm, double trackDurationSeconds)
        {
            var beats = BuildBeatEnergyCurve(spectralMap, bpm);
            if (beats.Count == 0)
                return Math.Max(0, trackDurationSeconds - 10.0);

            // Work primarily on the last half of the track
            int startIndex = beats.Count / 2;
            var lastHalf = beats.Skip(startIndex).ToList();
            if (lastHalf.Count == 0)
                lastHalf = beats;

            var lastHalfE = lastHalf.Select(b => b.Energy).OrderBy(x => x).ToList();
            double median = lastHalfE[lastHalfE.Count / 2];

            // Smoothed energy for lastHalf
            int smoothWindow = 3;
            double[] smoothE = new double[lastHalf.Count];
            for (int i = 0; i < lastHalf.Count; i++)
            {
                int s = Math.Max(0, i - smoothWindow);
                int e = Math.Min(lastHalf.Count - 1, i + smoothWindow);
                int len = e - s + 1;
                double sum = 0.0;
                for (int j = s; j <= e; j++)
                    sum += lastHalf[j].Energy;
                smoothE[i] = sum / len;
            }

            // Derivative of smoothed energy (within lastHalf)
            double[] dE = new double[lastHalf.Count];
            dE[0] = 0.0;
            for (int i = 1; i < lastHalf.Count; i++)
                dE[i] = smoothE[i] - smoothE[i - 1];

            double absMaxNeg = dE.Min(v => Math.Min(0.0, v)); // most negative value (<=0)
            double dropThresh = absMaxNeg * 0.5;              // half of the strongest negative drop

            double highLevel = median * 1.1;
            double lowLevel = median * 0.6;

            int plateauBeats = 8; // ~2 bars of groove
            int tailBeats = 8;    // ~2 bars of low-energy tail

            // Scan from the end backwards in lastHalf indices
            for (int i = lastHalf.Count - 2; i >= plateauBeats + tailBeats; i--)
            {
                bool strongDrop = dE[i] <= dropThresh;
                if (!strongDrop)
                    continue;

                int plateauStart = i - plateauBeats + 1;
                int tailStart = i + 1;
                if (plateauStart < 0 || tailStart + tailBeats > lastHalf.Count)
                    continue;

                var plateau = lastHalf.Skip(plateauStart).Take(plateauBeats).ToList();
                var tail = lastHalf.Skip(tailStart).Take(tailBeats).ToList();

                double plateauMin = plateau.Min(b => b.Energy);
                double plateauMax = plateau.Max(b => b.Energy);
                double tailMax = tail.Max(b => b.Energy);

                if (plateauMin > highLevel && tailMax < lowLevel)
                {
                    // plateau should be relatively stable
                    if (plateauMax < plateauMin * 1.3)
                    {
                        var beat = plateau.Last();
                        // keep a small safety margin before absolute end
                        return Math.Min(beat.Time, Math.Max(0, trackDurationSeconds - 5.0));
                    }
                }
            }

            // fallback: a bit before the end
            return Math.Max(0, trackDurationSeconds - 10.0);
        }

        public static float CalculateCompatibility(TrackData current, TrackData next)
        {
            float bpmDiff = Math.Abs(current.Bpm - next.Bpm);
            float bpmScore = Math.Max(0, 1.0f - (bpmDiff / 15.0f));
            float harmonicScore = 0.5f;

            if (current.Key != null && next.Key != null)
            {
                if (current.Key == next.Key) harmonicScore = 1.0f;
                else if (IsHarmonicNeighbor(current.Key, next.Key)) harmonicScore = 0.8f;
                else harmonicScore = 0.1f;
            }

            return (harmonicScore * 0.6f) + (bpmScore * 0.4f);
        }

        public static bool IsHarmonicNeighbor(string key1, string key2)
        {
            if (string.IsNullOrEmpty(key1) || string.IsNullOrEmpty(key2)) return false;
            if (key1 == key2) return true;

            try
            {
                int num1 = int.Parse(key1[0..^1]);
                char let1 = key1.Last();
                int num2 = int.Parse(key2[0..^1]);
                char let2 = key2.Last();

                if (num1 == num2 && let1 != let2) return true;
                if (let1 == let2)
                {
                    if (Math.Abs(num1 - num2) == 1) return true;
                    if ((num1 == 12 && num2 == 1) || (num1 == 1 && num2 == 12)) return true;
                }
            }
            catch { return false; }
            return false;
        }

        public static double CalculateIntroWindow(TrackData track)
        {
            if (track.SpectralMap == null || track.SpectralMap.Count == 0) return 0;
            int startIndex = (int)(track.MixInPoint * 10);
            int timeIndex = track.SpectralMap[0].Length - 1;

            // Definér energi som sum af alle bånd undtagen tid
            float InitialEnergyAt(int idx)
            {
                float sum = 0;
                for (int b = 0; b < timeIndex; b++) sum += track.SpectralMap[idx][b];
                return sum;
            }

            float initialEnergy = InitialEnergyAt(startIndex);

            for (int i = startIndex + 10; i < startIndex + 300 && i < track.SpectralMap.Count; i++)
            {
                float currentEnergy = InitialEnergyAt(i);
                if (currentEnergy > initialEnergy * 1.5f || currentEnergy > 0.4f)
                    return track.SpectralMap[i][timeIndex] - track.MixInPoint;
            }
            return 8.0;
        }

        public static int CalculateIntuitiveFade(TrackData current, TrackData next)
        {
            double introWindow = CalculateIntroWindow(next);
            double durationSecs = 8.0;

            if (introWindow > 12) durationSecs = 10.0;
            if (introWindow < 5) durationSecs = 4.0;

            if (current.Key != null && next.Key != null)
            {
                if (IsHarmonicNeighbor(current.Key, next.Key)) durationSecs *= 1.4;
                else durationSecs *= 0.6;
            }
            else
            {
                durationSecs = Math.Clamp(durationSecs, 4.0, 8.0);
            }

            return (int)(durationSecs * 1000);
        }
    }
}