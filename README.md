# AIDJ – Spectral AI DJ Engine

AIDJ is an experimental AI-powered DJ engine that:

- **Preprocesses audio tracks** offline into spectral maps, BPM, key, energy, and mix points.
- **Chooses the next track** based on harmony, BPM, and energy.
- **Plans and plays transitions** between two tracks using a heuristic, spectral, beat-aware engine.
- **Logs “weird” mixes** so you can later inspect *why* a transition felt wrong.

The goal is to move from “hard-coded crossfades” to an engine that has enough musical context to make transitions that *feel* intuitive, and to be **explainable** when they don’t.

---

## 1. Repository structure

```text
AIDJ.sln
AIDJ.Core/          # Analysis, models, heuristics (pure library)
  Models/
    TrackData.cs
    AnalysisContainer.cs
  Services/
    TrackAnalysisService.cs
    TrackSelection.cs
    TransitionPlanning.cs
    TransitionService.cs
    DjEngine.cs
    MusicLibraryService.cs
    ITrackAnalysisService.cs

AIDJ/               # Console app + debug visualizer
  Program.cs
  ConsoleVisualizer.cs
  TransitionLogger.cs

MusicLibrary/       # Local audio files (*.mp3) and their JSON analyses (*.mp3.json)
```

---

## 2. Data model: how a track looks inside AIDJ

Each audio file in `MusicLibrary/` has a corresponding JSON sidecar (`.mp3.json`) created by `TrackAnalysisService.GenerateFullAnalysis`.

### 2.1 `AnalysisContainer` (on disk)

```csharp
public class AnalysisContainer
{
    public string Key { get; set; }        // Camelot key (e.g. "8A", "8B")
    public float Bpm { get; set; }         // Detected BPM
    public List<float[]> Map { get; set; } // Spectral map
}
```

- `Map` is a list of frames, sampled every 0.1s:
  - `Map[i][0]` – bass band energy
  - `Map[i][1]` – mid band energy
  - `Map[i][2]` – high band energy
  - `Map[i][3]` – time in seconds

Visually, imagine the spectral map like this:

```text
time (s) →
0       5       10      15      20      25 ...

bass:  ▁▂▃▄▅▆▇██████▇▆▅▄▄▂▂▁＿＿＿＿＿＿＿＿＿＿＿＿＿＿
mids:  ▁▂▃▄▄▅▆▇▇▇▇▇▆▅▅▃▃▂▂▁＿＿＿＿＿＿＿＿＿＿＿＿＿＿
highs: ▁▁▂▂▃▄▄▄▅▆▆▅▄▄▃▂▂▁＿＿＿＿＿＿＿＿＿＿＿＿＿＿
```

### 2.2 `TrackData` (in memory)

```csharp
public class TrackData
{
    public string Title { get; set; }
    public string Path { get; set; }
    public float Bpm { get; set; }
    public TimeSpan Duration { get; set; }
    public float Energy { get; set; }          // 0..1, avg bass+mid+high
    public double MixInPoint { get; set; }     // seconds
    public double MixOutPoint { get; set; }    // seconds
    public List<float[]> SpectralMap { get; set; }
    public string Key { get; set; } = null;    // Camelot key
}
```

`TrackAnalysisService.AnalyzeTrack`:

1. Ensures `<track>.mp3.json` exists (creates it if missing or old format).
2. Loads `AnalysisContainer` from JSON.
3. Uses TagLib to get title and duration.
4. Computes:
   - `Energy` – average of `bass + mids + highs` over all frames (clamped to 0..1).
   - `MixInPoint` – first time where spectral energy crosses a threshold.
   - `MixOutPoint` – bass-drop based point near the end.

---

## 3. Offline analysis pipeline

> For background on some of the signal-processing concepts mentioned here, see:
> - [Fast Fourier transform (FFT)](https://en.wikipedia.org/wiki/Fast_Fourier_transform)
> - [Spectrogram / time–frequency representation](https://en.wikipedia.org/wiki/Spectrogram)
> - [Spectral density / spectrum](https://en.wikipedia.org/wiki/Spectral_density)
> - [Chroma feature / chromagram](https://en.wikipedia.org/wiki/Chroma_feature)
> - [Root mean square (RMS) and energy in audio](https://en.wikipedia.org/wiki/Root_mean_square#RMS_amplitude)
> - [Musical key](https://en.wikipedia.org/wiki/Musical_key) and Camelot-style DJ key systems

### 3.1 FFT and spectral bands

For each track, AIDJ creates a **decode-only** stream with Bass and, on each analysis step, calls:

- `Bass.ChannelGetData` with `DataFlags.FFT2048`.

This returns a 2048-point FFT buffer (only the first half is meaningful due to symmetry). The code uses a 1024-length float array (`fft`) as the working buffer, which corresponds to the positive-frequency bins.

Very roughly:

- Lower indices in `fft` → lower frequencies (sub/bass region).
- Higher indices → higher frequencies (mids and highs).

To keep things simple and fast, AIDJ **compresses the full FFT** into three broad bands:

- **Bass**: `fft[0..9]` (first 10 bins)
- **Mids**: `fft[10..109]` (next ~100 bins)
- **Highs**: `fft[110..end]` (rest)

Why this design?

- For **DJ decisions** (mix in/out, energy, groove), you rarely need a full high-resolution spectrum.
- Three bands map nicely onto how DJs think about a track:
  - bass (kick/bassline),
  - mids (body, chords, some vocal),
  - highs (hats, air).
- It keeps the JSON small and the heuristics interpretable while still tracking how the track “breathes” over time.

Every 0.1 seconds (`snapshotInterval`), AIDJ:

1. Gets FFT data.
2. Computes:

   ```text
   bass  = avg(fft[0..9])
   mids  = avg(fft[10..109])
   highs = avg(fft[110..])
   time  = currentSeconds
   ```

3. Appends `[bass, mids, highs, time]` to the spectral map.

### 3.2 Chroma analysis and key detection

In parallel with the spectral bands, AIDJ builds a **chroma vector**:

- For each FFT bin within ~50–4000 Hz:
  - Map frequency → note index.
  - Fold into 12 chroma bins (0..11).
- At the end:
  - Find the strongest root note.
  - Compare minor vs major third to decide minor/major.
  - Map to a Camelot label (e.g. `"8A"`, `"8B"`) via lookup tables.

### 3.3 Automatic, beat-aware mix-in / mix-out points

Instead of hard thresholds on individual frames, AIDJ now works with a **per-beat energy curve** and its **derivative**, derived from the spectral map and BPM.

At a high level:

1. Compute beat length: `beatLen = 60 / BPM`.
2. For each beat window `[k*beatLen, (k+1)*beatLen)`, average `bass + mids + highs` over all frames in that window → per-beat energy `E[k]`.
3. Smooth `E[k]` over a few beats to get a stable curve.
4. Compute a discrete derivative `dE[k] = E_smooth[k] - E_smooth[k-1]`.

**MixInPoint (beat-aware, derivative + plateau):**

- Look at the **first half** of the track’s beat energy to get a robust median intro level.
- Use the derivative `dE[k]` to find **strong positive changes** (big rises in energy) – candidates for where the groove “drops in”.
- For each candidate index `k`:
  - Check the next ~8 beats (≈2 bars) as a plateau window:
    - per-beat energy stays above a level clearly higher than the intro median,
    - energy within that window is relatively stable (no huge internal ramp-up).
- The mix-in point is set to the **time of the first beat** of the first candidate that passes this plateau test.
- Fallback: time of the first beat if no suitable candidate is found.

**MixOutPoint (beat-aware, derivative + plateau):**

- Focus on the **last half** of the track’s beat energy and compute a median late-track energy.
- Use the derivative `dE[k]` (in the late region) to find **strong negative changes** – candidates for where energy drops into an outro.
- For each candidate (scanning from the end backwards):
  - Check a preceding plateau window (~8 beats) and a following tail window (~8 beats):
    - plateau: high and relatively stable energy, clearly above the late-track median,
    - tail: consistently lower energy, suggesting an outro or breakdown.
- The mix-out point is placed near the **end of that last plateau**, but slightly before the actual drop, with an additional safety margin a few seconds before the absolute end of the track.
- Fallback: a few seconds before the end of the track if no clear candidate satisfies these conditions.

Visually:

```text
Track timeline (seconds)

0                  MixInPoint                  MixOutPoint          End
|----------------------|-----------------------------|----------------|

Intro (quiet)   →   Groove/Body   →   Tail / outro (drop in bass)
```

---

## 4. How the engine chooses the next track

### 4.1 `DjContext` and `TrackSelector`

```csharp
public class DjContext
{
    // Desired energy level for the next track (0..1)
    public float TargetEnergy { get; set; }
}
```

`TrackSelector.ScoreNext(current, next, ctx)` combines:

1. **Compatibility (harmony + BPM)** via `TrackAnalysisService.CalculateCompatibility`:
   - Same Camelot key → 1.0
   - Camelot neighbor → 0.8
   - Otherwise → 0.1
   - Weighted with a BPM-distance term.

2. **BPM jump**:
   - Soft penalty as BPM difference grows (no hard cutoff).

3. **Energy match**:
   - How close is `next.Energy` to `ctx.TargetEnergy`?

Final score:

```text
score = 0.5 * compat + 0.3 * energyScore + 0.2 * bpmScore
```

`TrackSelector.ChooseNext` always picks the highest scoring track from the remaining library, so over time everything will be played.

---

## 5. Transition planning: how A → B is decided

### 5.1 Overview

The core of the DJ logic is the `HeuristicTransitionPlanner`:

```text
Track A                               Track B
--------- MixOutPoint ---------------------- MixInPoint ----------->

         |<------ duration D ------>|
         |------ tail of A ---------|
                         |--------- windowSeconds ----------->

                       StartA             StartB
```

Given a `TransitionContext`:

```csharp
public class TransitionContext
{
    public TrackData Current { get; set; }  // A
    public TrackData Next { get; set; }     // B

    public float CurrentEnergy { get; set; }
    public float TargetEnergy { get; set; }

    public float Intensity { get; set; }
    public double PreferredDurationSeconds { get; set; }
}
```

The planner:

1. Chooses a **base fade duration** from `CalculateIntuitiveFade(current, next)`.
2. Adjusts for **harmonic merges** (longer when keys match nicely).
3. Chooses `StartOffsetA` (where to start fading out A).
4. Chooses `StartOffsetB` (where to fade in B) using spectral + groove matching.
5. Builds a time series of `TransitionPoint`s (volume, filters, tempo).

### 5.2 Intuitive fade duration

`TrackAnalysisService.CalculateIntuitiveFade(current, next)`:

1. Computes **intro window** of B (how long until it really “kicks in” after `MixInPoint`).
2. Uses rules like:
   - Short intro → shorter fade.
   - Long intro → longer fade.
3. Modifies duration based on harmonic relationship:
   - Harmonic neighbors → ×1.4 (longer merge).
   - Non-harmonic → ×0.6 (shorter, more surgical mix).

This gives a base duration (seconds) which is blended with a preferred duration (e.g. 8s).

### 5.3 Harmonic merge and `StartOffsetA`

```csharp
double baseFade = TrackAnalysisService.CalculateIntuitiveFade(current, next) / 1000.0;
double desired = ctx.PreferredDurationSeconds > 0 ? ctx.PreferredDurationSeconds : baseFade;
double duration = 0.5 * baseFade + 0.5 * desired;

bool harmonicMerge = current.Key != null && next.Key != null &&
                     TrackAnalysisService.IsHarmonicNeighbor(current.Key, next.Key);
if (harmonicMerge)
    duration *= 1.5;    // extend fade if harmony is good

double startOffsetA = current.MixOutPoint - duration * 0.8;
if (startOffsetA < 0) startOffsetA = 0;
```

For harmonic mixes, overlaps are allowed to be longer; start the fade before `MixOutPoint` to get some tail.

### 5.4 Finding `StartOffsetB` from the spectral maps

Goal: find a segment in B that **“feels like”** the tail of A in terms of groove, spectrum, and energy.

1. Build a window over the end of A:
   - `[aStart, aEnd]` where `aEnd = current.MixOutPoint`.
   - Compute:
     - Average spectrum `aAvgSpec = [bass, mids, highs]`.
     - Average energy `aEnergy`.

2. Build a **beat sequence** `seqA` over the tail of A:
   - Try 16 beats, fallback to 8 or 4 if not enough data.
   - For each beat, compute `[bass, mids, highs]`.

3. Define candidate region in B:
   - Start at `next.MixInPoint + 5s`.
   - Stop before the very end (`Duration - windowSeconds - buffer`).

4. For each candidate time `t`` (on a beat grid):

   - Compute average spectrum/energy over `[t, t + windowSeconds]` in B.
   - If `seqA` is available, build `seqB` and compute a distance between `seqA` and `seqB`.
   - Compute scores:
     - **patternScore**: similarity of beat sequences (groove).
     - **continuityScore**: spectral distance between `aAvgSpec` and `bAvgSpec`.
     - **energyScore**: energy difference vs desired energy change.
     - **tailPenalty**: penalty for starting too close to the end of B.

5. Choose the `t` with the best combined score as `StartOffsetB`.

### 5.5 Volume and filter envelopes

Once `duration`, `StartOffsetA`, and `StartOffsetB` are chosen, the planner builds a `TransitionSpec`:

```csharp
public class TransitionSpec
{
    public double DurationSeconds { get; set; }
    public double StartOffsetA { get; set; }
    public double StartOffsetB { get; set; }
    public List<TransitionPoint> Points { get; set; }
}
```

Each `TransitionPoint` contains:

- `TimeSeconds` (relative to transition start)
- `VolumeA`, `VolumeB`
- `FilterCutoffA`, `FilterCutoffB` (for high-pass on A, low-pass on B)
- `TempoPercentB` (how much B should be tempo-adjusted toward A)

Visually, during the transition:

```text
Volume
1.0 |A\\\\\\____________________
    |      \\\
    |       \\\
    |        \\\
0.0 +----------------------------
      0        D/2            D     Time (within transition)

1.0 |           /BBBBBBBBBBBBB
    |         //
    |       //
    |     //
0.0 +----------------------------
      0        D/2            D
```

---

## 6. Executing the transition in audio

`TransitionService.PlayPlannedTransition(outgoingHandle, incomingHandle, spec)`:

1. Seek B to `spec.StartOffsetB` (if > 0).
2. Set initial volumes and filter cutoffs based on the first `TransitionPoint`.
3. Start both streams with Bass.
4. For each point in `spec.Points`:
   - Update volumes for A and B.
   - Update filter center frequencies for A and B.
   - Adjust B’s tempo (`ChannelAttribute.Tempo`) toward A’s BPM.
   - Wait `dt` between points (`p.TimeSeconds - lastTime`).
5. At the end:
   - Set A’s volume to 0, stop and free A.
   - B continues playing as the new current track.

---

## 7. Console visualizer and logging

### 7.1 Runtime visualizer

`ConsoleVisualizer.Render` shows:

- NOW/NEXT tracks: title, key, BPM.
- 3-band VU meter from the spectral map at the current playback time.
- Intro window of NEXT and intuitive fade duration.
- Groove pattern size (`LastPatternBeats`).
- A “mix confidence” percentage based on compatibility (color coded).

Keyboard controls (`ConsoleVisualizer.HandleInput`):

- `J` – jump to 10 seconds before `MixOutPoint` on the current track.
- `L` – log the current context to `transition_log.txt`.

### 7.2 Logging “weird” mixes

Whenever a mix feels wrong, press **L**.

`TransitionLogger.Log` writes a line like:

```text
[2025-12-30 23.07.27.581] POS=44.65s |
CURRENT="Track A" (BPM=125.2, Key=8B, Path=..., MixIn=0.00, MixOut=185.22) |
NEXT="Track B" (BPM=128.0, Key=8B, Path=..., MixIn=15.14, MixOut=464.31) |
CurrentSpec=...A.mp3.json | NextSpec=...B.mp3.json |
Compat=0.928 HarmonicNeighbor=True IntroWindowNext=7.48s IntuitiveFade=11.20s |
PatternBeats=16 |
Planner[BaseFade=14.00s, Preferred=8.00s, Final=18.15s, HarmonicMerge=True,
StartA=161.63s, StartB=7.19s, EntryPosB=0.04, Window=13.20s, DesiredΔE=-0.056]
```

You can then:

1. Load `CurrentSpec` and `NextSpec` JSON in a notebook/tool.
2. Plot spectral bands vs time, with vertical lines at:
   - `MixInPoint` / `MixOutPoint`,
   - `StartOffsetA` / `StartOffsetB`.
3. See exactly how the overlap was constructed and why it might feel off.

---

## 8. Limitations and roadmap

Current limitations:

- No full beat grid / downbeat detection – only BPM and coarse spectral groove matching.
- Drum phases can still feel off, especially when both tracks have strong but different patterns.
- Transitions are heuristic, not ML-learned; they’re designed to be understandable and tweakable.

Planned / possible extensions:

- Proper beat-grid and downbeat detection to align StartB more precisely with musical “1”s.
- Stem separation (e.g. vocal vs music) to:
  - avoid mixing out in the middle of a sentence,
  - deliberately extend vocal phrases between tracks.
- A small GUI or notebook-based inspector to:
  - browse `transition_log.txt`,
  - open and visualize the two spectral JSONs for each logged case.

---

## 9. Running the project

1. Put your `.mp3` files in `AIDJ/MusicLibrary/` (these should be `.gitignore`’d; only JSONs and code live in Git).
2. Build and run the `AIDJ` console project (e.g. from Rider or `dotnet run`).
3. Watch the console visualizer:
   - See NOW/NEXT, BPM, keys, mix in/out, and spectral bars.
   - Press `J` to jump near mix-out.
   - Press `L` when something sounds off to log the context.
4. Inspect `transition_log.txt` and the `.json` analyses to evolve the heuristics.
