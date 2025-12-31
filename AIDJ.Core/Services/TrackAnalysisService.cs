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

            // Simpel energi-måling: gennemsnitlig sum af bass/mid/high over hele tracket
            float energy = 0f;
            if (container.Map != null && container.Map.Count > 0)
            {
                energy = container.Map.Average(f => f[0] + f[1] + f[2]);
                if (energy < 0f) energy = 0f;
                if (energy > 1f) energy = 1f; // clamp til [0,1] for scoring
            }

            return new TrackData
            {
                Title = title,
                Bpm = container.Bpm,
                Key = container.Key,
                Path = filePath,
                Duration = tfile.Properties.Duration,
                Energy = energy,
                SpectralMap = container.Map,
                MixInPoint = FindMixInPoint(container.Map),
                MixOutPoint = FindMixOutPoint(container.Map)
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

        public static double FindMixOutPoint(List<float[]> spectralMap)
        {
            int searchStart = (int)(spectralMap.Count * 0.80);
            for (int i = spectralMap.Count - 2; i > searchStart; i--)
            {
                if (spectralMap[i][0] > 0.1f && spectralMap[i + 1][0] < 0.02f)
                    return spectralMap[i][spectralMap[i].Length - 1];
            }
            return spectralMap.Last()[spectralMap.Last().Length - 1] - 10;
        }

        public static double FindMixInPoint(List<float[]> spectralMap)
        {
            int searchLimit = Math.Min(spectralMap.Count, 600);
            for (int i = 0; i < searchLimit; i++)
            {
                // Brug de lavere/mellemste bånd som indikator for "start på groove"
                if (spectralMap[i][0] > 0.15f || spectralMap[i][1] > 0.2f || spectralMap[i][2] > 0.2f)
                    return spectralMap[i][spectralMap[i].Length - 1];
            }
            return 0.0;
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