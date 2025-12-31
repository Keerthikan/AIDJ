using System;
using System.Collections.Generic;

public class TrackData
{

    public string Title { get; set; }
    public string Path { get; set; }
    public float Bpm { get; set; }
    public TimeSpan Duration { get; set; }
    public float Energy { get; set; }
    public double MixInPoint { get; set; }
    public double MixOutPoint { get; set; }
    // SpectralMap: each entry is a float[] with N bands + last element = time in seconds
    public List<float[]> SpectralMap { get; set; }
    public string Key { get; set; } = null; // Default til 8A (Am), hvis intet findes
}
