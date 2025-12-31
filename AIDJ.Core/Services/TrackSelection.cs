using System;
using System.Collections.Generic;
using System.Linq;

namespace AIDJ.Core.Services
{
    public class DjContext
    {
        // Ønsket energi-niveau for det næste track (0..1)
        public float TargetEnergy { get; set; }
    }

    public static class TrackSelector
    {
        public static float ScoreNext(TrackData current, TrackData next, DjContext ctx)
        {
            if (current == null || next == null) return float.NegativeInfinity;

            // 1) Harmoni + BPM (eksisterende compatibility)
            float compat = TrackAnalysisService.CalculateCompatibility(current, next); // 0..1

            // 2) BPM-hop: blød straf, men ingen hard cut
            float bpmDiff = Math.Abs(current.Bpm - next.Bpm);
            float bpmScore = Math.Max(0f, 1.0f - bpmDiff / 30.0f); // 0 ved ~30 BPM forskel

            // 3) Energi-match i forhold til ønsket target
            float desiredEnergy = ctx?.TargetEnergy ?? current.Energy;
            float thisEnergy = next.Energy;
            if (desiredEnergy < 0f) desiredEnergy = 0f;
            if (desiredEnergy > 1f) desiredEnergy = 1f;
            if (thisEnergy < 0f) thisEnergy = 0f;
            if (thisEnergy > 1f) thisEnergy = 1f;

            float energyDiff = Math.Abs(thisEnergy - desiredEnergy); // 0..1
            float energyScore = 1.0f - energyDiff; // 1 = perfekt match, 0 = helt ved siden af

            // 4) Kombiner til samlede score
            return
                compat      * 0.5f +
                energyScore * 0.3f +
                bpmScore    * 0.2f;
        }

        public static TrackData ChooseNext(IReadOnlyList<TrackData> library, TrackData current, DjContext ctx)
        {
            if (library == null || library.Count == 0 || current == null)
                return null;

            // Vælg altid et track (øverste score) – så alle i biblioteket til sidst bliver spillet
            return library
                .OrderByDescending(t => ScoreNext(current, t, ctx))
                .First();
        }
    }
}
