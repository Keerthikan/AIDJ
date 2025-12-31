using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;

namespace AIDJ.Core.Services
{
    public class DjEngine
    {
        private readonly List<TrackData> _library;
        private readonly TransitionService _transitionService;
        private readonly ITransitionPlanner _planner;
        private readonly DjContext _context;

        public TrackData CurrentTrack { get; private set; }
        public TrackData NextTrack { get; private set; }
        public int CurrentHandle { get; private set; }
        public int NextHandle { get; private set; }

        public DjEngine(List<TrackData> library, TransitionService transitionService, ITransitionPlanner planner)
        {
            _library = library ?? throw new ArgumentNullException(nameof(library));
            _transitionService = transitionService ?? throw new ArgumentNullException(nameof(transitionService));
            _planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _context = new DjContext();
        }

        /// <summary>
        /// Vælg første track (langsommeste BPM), fjern det fra biblioteket og start afspilning.
        /// </summary>
        public bool InitializeFirstTrack()
        {
            if (_library.Count == 0)
                return false;

            CurrentTrack = _library.OrderBy(t => t.Bpm).First();
            _library.Remove(CurrentTrack);

            CurrentHandle = Bass.CreateStream(CurrentTrack.Path, 0, 0, BassFlags.Default);
            if (CurrentHandle == 0)
                return false;

            Bass.ChannelPlay(CurrentHandle);
            return true;
        }

        /// <summary>
        /// Vælg næste track baseret på TrackSelector (harmoni, BPM, energi).
        /// </summary>
        public void SelectNextTrack()
        {
            if (_library.Count == 0 || CurrentTrack == null)
            {
                NextTrack = null;
                return;
            }

            _context.TargetEnergy = CurrentTrack.Energy;
            NextTrack = TrackSelector.ChooseNext(_library, CurrentTrack, _context);
        }

        /// <summary>
        /// Forbered transition ved at oprette næste stream og generere en TransitionSpec.
        /// </summary>
        public TransitionSpec PrepareTransition()
        {
            if (CurrentTrack == null || NextTrack == null)
                return null;

            NextHandle = Bass.CreateStream(NextTrack.Path, 0, 0, BassFlags.Default);
            if (NextHandle == 0)
                return null;

            var ctx = new TransitionContext
            {
                Current = CurrentTrack,
                Next = NextTrack,
                CurrentEnergy = CurrentTrack.Energy,
                TargetEnergy = NextTrack.Energy,
                Intensity = 0.7f,
                PreferredDurationSeconds = 8.0
            };

            return _planner.Plan(ctx);
        }

        /// <summary>
        /// Efter en transition er startet og den gamle sang er faded ud, flyt fokus til næste track.
        /// </summary>
        public void AdvanceAfterTransition()
        {
            CurrentTrack = NextTrack;
            CurrentHandle = NextHandle;

            if (NextTrack != null)
            {
                _library.Remove(NextTrack);
            }

            NextTrack = null;
            NextHandle = 0;
        }

        /// <summary>
        /// Hop til et stykke før MixOutPoint på det nuværende track.
        /// </summary>
        public void JumpToPreMixOut(double secondsBefore)
        {
            if (CurrentTrack == null || CurrentHandle == 0)
                return;

            double target = Math.Max(0, CurrentTrack.MixOutPoint - secondsBefore);
            long bytes = Bass.ChannelSeconds2Bytes(CurrentHandle, target);
            Bass.ChannelSetPosition(CurrentHandle, bytes);
        }
    }
}
