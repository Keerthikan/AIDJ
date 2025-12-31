using System;
using System.Collections.Generic;
using System.IO;

namespace AIDJ.Core.Services
{
    public class MusicLibraryService
    {
        private readonly TrackAnalysisService _analysisService;

        public MusicLibraryService(TrackAnalysisService analysisService)
        {
            _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        }

        public List<TrackData> LoadLibrary(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                throw new ArgumentException("Library path must be provided", nameof(libraryPath));

            if (!Directory.Exists(libraryPath))
                throw new DirectoryNotFoundException($"Library path not found: {libraryPath}");

            var files = Directory.GetFiles(libraryPath, "*.mp3");
            var library = new List<TrackData>();

            foreach (var file in files)
            {
                // Bevar den gamle konsoloutput-adfærd: vis hvert track der analyseres
                Console.Write($"[Processing] {Path.GetFileName(file)}... ");

                var data = _analysisService.AnalyzeTrack(file);
                library.Add(data);

                Console.WriteLine("Ready.");
            }

            return library;
        }
    }
}
