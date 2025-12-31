using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ManagedBass;
using AIDJ.Core.Services;

namespace AIDJ
{
    class Program
    {
        static TransitionService transitionService = new TransitionService();
        static ITransitionPlanner transitionPlanner = new HeuristicTransitionPlanner();

        static async Task Main(string[] args)
        {
            Console.Clear();
            Console.WriteLine("--- AI DJ CONSOLE (Spectral Sync Mode) ---");

            // macOS library path fix
            Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", AppContext.BaseDirectory);

            if (!Bass.Init(-1, 44100, DeviceInitFlags.Default))
            {
                Console.WriteLine("Bass Init Failed.");
                return;
            }

            try
            {
                var analysisService = new TrackAnalysisService();
                // Din sti til musikken
                string libraryPath = "/Users/keerthikanratnarajah/RiderProjects/AIDJ/AIDJ/MusicLibrary";

                var libraryService = new MusicLibraryService(analysisService);

                Console.WriteLine($"Loading library from: {libraryPath}");
                var library = libraryService.LoadLibrary(libraryPath);

                if (library.Count == 0)
                {
                    Console.WriteLine("No tracks found.");
                    return;
                }

                // Opret DJ-engine til at styre afspilning og track-state
                var engine = new DjEngine(library, transitionService, transitionPlanner);

                if (!engine.InitializeFirstTrack())
                {
                    Console.WriteLine("Failed to start first track.");
                    return;
                }

                while (engine.CurrentTrack != null)
                {
                    var currentTrack = engine.CurrentTrack;

                    // FIND NÆSTE SANG (Smart Selection med flere faktorer)
                    engine.SelectNextTrack();
                    var nextTrack = engine.NextTrack;

                    bool transitioned = false;

                    // MONITORING LOOP
                    while (Bass.ChannelIsActive(engine.CurrentHandle) == PlaybackState.Playing)
                    {
                        double pos = Bass.ChannelBytes2Seconds(engine.CurrentHandle, Bass.ChannelGetPosition(engine.CurrentHandle));

                        // Håndter konsolinput (fx jump til 10 sek før mix-out eller log 'L')
                        ConsoleVisualizer.HandleInput(engine, pos);

                        // Find det rigtige tidspunkt i spectral mappet (sidste element i snapshot er tid)
                        var currentSnapshot = currentTrack.SpectralMap?
                            .FirstOrDefault(s => s[s.Length - 1] >= (float)pos);

                        if (currentSnapshot != null)
                        {
                            ConsoleVisualizer.Render(currentTrack, nextTrack, pos, currentSnapshot);
                        }

                        // TRANSITION LOGIK
                        if (pos >= currentTrack.MixOutPoint && !transitioned && nextTrack != null)
                        {
                            transitioned = true;

                            // Forbered transition (opret stream for næste track og generer plan)
                            var spec = engine.PrepareTransition();
                            if (spec != null)
                            {
                                _ = transitionService.PlayPlannedTransition(engine.CurrentHandle, engine.NextHandle, spec);
                            }

                            break;
                        }

                        await Task.Delay(50); // CPU-venlig opdateringshastighed
                    }

                    // Skift sang-fokus via engine
                    engine.AdvanceAfterTransition();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nCritical Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                Bass.Free();
            }
        }

        // Visualizer-logik er flyttet til ConsoleVisualizer.Render
    }
}