using System.Threading.Tasks;

public interface ITrackAnalysisService
{
    Task<AnalysisResult> AnalyzeTrackAsync(string filePath);
}

public record AnalysisResult(string Title, double Duration, float Energy, float[] Spectrum);