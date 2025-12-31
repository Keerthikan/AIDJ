using ManagedBass;
using ManagedBass.Fx;
using System.Threading.Tasks;
using AIDJ.Core.Services;

namespace AIDJ.Core.Services
{
    public class TransitionService
    {
        // 1. STANDARD CROSSFADE (The Blend)
        public async Task Crossfade(int outgoingHandle, int incomingHandle, int durationMs)
        {
            // 1. Gør Sang B klar med lav volumen
            Bass.ChannelSetAttribute(incomingHandle, ChannelAttribute.Volume, 0);

            // 2. Valgfrit: Sænk bassen på Sang B i starten (High-Pass filter)
            // Dette kræver Bass.ChannelSetFX med et Peak/LowShelf filter

            Bass.ChannelPlay(incomingHandle);

            // 3. Den flydende overgang
            Bass.ChannelSlideAttribute(outgoingHandle, ChannelAttribute.Volume, 0, durationMs);
            Bass.ChannelSlideAttribute(incomingHandle, ChannelAttribute.Volume, 1, durationMs);

            // 4. "The Sweet Spot" - Halvvejs gennem mixet
            await Task.Delay(durationMs / 2);

            // Her kunne man lave det "intuitive" skift, hvor Sang B overtager kontrollen
            // f.eks. ved at fjerne EQ-filtreringen på Sang B nu.

            await Task.Delay(durationMs / 2 + 500);

            Bass.ChannelStop(outgoingHandle);
            Bass.StreamFree(outgoingHandle);
        }

        // 2. ECHO OUT (The Bail-out)
        public async Task EchoOut(int currentStream, int nextStream)
        {
            // Add Echo effect to current track
            int echo = Bass.ChannelSetFX(currentStream, EffectType.Echo, 1);
            var echoParams = new EchoParameters { fFeedback = 0.7f, fWetMix = 0.5f, fDelay = 0.5f };
            Bass.FXSetParameters(echo, echoParams);

            // Quickly slide volume down but keep echo tail
            Bass.ChannelSlideAttribute(currentStream, ChannelAttribute.Volume, 0, 1000);

            // Smash the next track in immediately
            Bass.ChannelPlay(nextStream);

            await Task.Delay(2000);
            Bass.ChannelStop(currentStream);
        }

        public async Task FilterSweep(int currentStream, int nextStream, int durationMs)
        {
            // Use FXType.BQF instead of EffectType.BiquadFilter
            int fxHandle = Bass.ChannelSetFX(currentStream, EffectType.BQF, 1);

            // Use BQFParameters instead of BiquadFilterParameters
            var filterParams = new BQFParameters
            {
                lFilter = BQFType.HighPass, // HighPass to sweep out the bass
                fCenter = 100f,
                fGain = 0,
                fBandwidth = 1f
            };
            Bass.FXSetParameters(fxHandle, filterParams);
            Bass.ChannelPlay(nextStream);

            // 3. Sweep the frequency
            int steps = 20;
            for (int i = 0; i < steps; i++)
            {
                filterParams.fCenter += (3000f - 100f) / steps;
                Bass.FXSetParameters(fxHandle, filterParams);

                await Task.Delay(durationMs / steps);
            }

            Bass.ChannelStop(currentStream);
        }

        // 3. PLANNED TRANSITION (Parametrisk AI plan)
        public async Task PlayPlannedTransition(int outgoingHandle, int incomingHandle, TransitionSpec spec)
        {
            if (spec == null || spec.Points == null || spec.Points.Count == 0)
                return;

            // Juster kun startposition for den INDGÅENDE sang (outgoing fortsætter fra nuværende position)
            if (spec.StartOffsetB > 0)
            {
                long bytesB = Bass.ChannelSeconds2Bytes(incomingHandle, spec.StartOffsetB);
                Bass.ChannelSetPosition(incomingHandle, bytesB);
            }

            // Sæt initielle volume
            var first = spec.Points[0];
            Bass.ChannelSetAttribute(outgoingHandle, ChannelAttribute.Volume, first.VolumeA);
            Bass.ChannelSetAttribute(incomingHandle, ChannelAttribute.Volume, first.VolumeB);

            // Opsæt simple high/low-pass filtre til A og B
            int fxA = Bass.ChannelSetFX(outgoingHandle, EffectType.BQF, 1);
            int fxB = Bass.ChannelSetFX(incomingHandle, EffectType.BQF, 1);

            var filterA = new BQFParameters
            {
                lFilter = BQFType.HighPass,
                fCenter = first.FilterCutoffA,
                fGain = 0,
                fBandwidth = 1f
            };

            var filterB = new BQFParameters
            {
                lFilter = BQFType.LowPass,
                fCenter = first.FilterCutoffB,
                fGain = 0,
                fBandwidth = 1f
            };

            Bass.FXSetParameters(fxA, filterA);
            Bass.FXSetParameters(fxB, filterB);

            // Start begge streams
            Bass.ChannelPlay(outgoingHandle);
            Bass.ChannelPlay(incomingHandle);

            double lastTime = first.TimeSeconds;
            for (int i = 1; i < spec.Points.Count; i++)
            {
                var p = spec.Points[i];
                double dt = p.TimeSeconds - lastTime;
                if (dt < 0) dt = 0;

                // Volume
                Bass.ChannelSetAttribute(outgoingHandle, ChannelAttribute.Volume, p.VolumeA);
                Bass.ChannelSetAttribute(incomingHandle, ChannelAttribute.Volume, p.VolumeB);

                // Filter sweeps
                filterA.fCenter = p.FilterCutoffA;
                filterB.fCenter = p.FilterCutoffB;
                Bass.FXSetParameters(fxA, filterA);
                Bass.FXSetParameters(fxB, filterB);

                // Tempo på B (samme semantik som eksisterende tempo-brug)
                float tempoDelta = p.TempoPercentB - 100f;
                Bass.ChannelSetAttribute(incomingHandle, ChannelAttribute.Tempo, tempoDelta);

                if (dt > 0)
                    await Task.Delay((int)(dt * 1000));

                lastTime = p.TimeSeconds;
            }

            // Afslut: fade A helt ud og lad B køre
            Bass.ChannelSetAttribute(outgoingHandle, ChannelAttribute.Volume, 0);
            Bass.ChannelStop(outgoingHandle);
            Bass.StreamFree(outgoingHandle);
        }
    }
}
