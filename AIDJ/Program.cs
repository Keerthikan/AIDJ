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

        // macOS: ensure native Bass libraries can be found via DYLD_LIBRARY_PATH
            Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", AppContext.BaseDirectory);

            if (!Bass.Init(-1, 44100, DeviceInitFlags.Default))
            {
                Console.WriteLine("Bass Init Failed.");
                return;
            }

            try
            {
                var analysisService = new TrackAnalysisService();
                // Path to the local music library
                string libraryPath = "/Users/keerthikanratnarajah/RiderProjects/AIDJ/AIDJ/MusicLibrary";

                var libraryService = new MusicLibraryService(analysisService);

                Console.WriteLine($"Loading library from: {libraryPath}");
                var library = libraryService.LoadLibrary(libraryPath);

                if (library.Count == 0)
                {
                    Console.WriteLine("No tracks found.");
                    return;
                }

                // Create the DJ engine to control playback and track state
                var engine = new DjEngine(library, transitionService, transitionPlanner);

                if (!engine.InitializeFirstTrack())
                {
                    Console.WriteLine("Failed to start first track.");
                    return;
                }

                while (engine.CurrentTrack != null)
                {
                    var currentTrack = engine.CurrentTrack;

                    // Find the next track using multi-factor smart selection
                    engine.SelectNextTrack();
                    var nextTrack = engine.NextTrack;

                    bool transitioned = false;

                    // Monitoring loop
                    while (Bass.ChannelIsActive(engine.CurrentHandle) == PlaybackState.Playing)
                    {
                        double pos = Bass.ChannelBytes2Seconds(engine.CurrentHandle, Bass.ChannelGetPosition(engine.CurrentHandle));

                        // Handle console input (e.g. jump 10 seconds before mix-out or log with 'L')
                        ConsoleVisualizer.HandleInput(engine, pos);

                        // Find the corresponding time slice in the spectral map (last element in snapshot is time)
                        var currentSnapshot = currentTrack.SpectralMap?
                            .FirstOrDefault(s => s[s.Length - 1] >= (float)pos);

                        if (currentSnapshot != null)
                        {
                            ConsoleVisualizer.Render(currentTrack, nextTrack, pos, currentSnapshot);
                        }

                        // Transition logic
                        if (pos >= currentTrack.MixOutPoint && !transitioned && nextTrack != null)
                        {
                            transitioned = true;

                            // Prepare transition (create stream for the next track and generate a plan)
                            var spec = engine.PrepareTransition();
                            if (spec != null)
                            {
                                _ = transitionService.PlayPlannedTransition(engine.CurrentHandle, engine.NextHandle, spec);
                            }

                            break;
                        }

                        await Task.Delay(50); // CPU-venlig opdateringshastighed
                    }

                    // Shift track focus via the engine
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

        // Visualizer logic has been moved to ConsoleVisualizer.Render
    }
}