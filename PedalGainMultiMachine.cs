// Pedal Gain Multi — ReBuzz managed effect machine (v2.0.2)
//
// A 1..24 channel mixer:
//   • Each input pin is a channel; the strip is Mono -> Mute -> Volume -> Pan
//   • Output 0 is the master mix; output k+1 is channel k's direct out,
//     taken post-pan but PRE-solo (Solo, Gain and Master Mute never reach it)
//   • Global parameters: Gain (dB), Master Mute, Inertia
//   • Track parameters:  Solo, Mute, Volume (dB), Pan, Mono
//   • Metering: per-channel pre/post peak and RMS, master true peak
//     (4x oversampled), K-weighted LUFS blocks and stereo correlation,
//     exposed through TakeMeters(slot)
//
// This file holds the parameters, the track/channel-count plumbing and the
// audio engine. The parameter panel lives in PedalGainMultiGUI.cs; the
// meter window and the loudness maths in PedalGainMultiMeterWindow.cs.
//
// v2.0.0 replaced the v1.x parameter layout entirely (v1 had Gain as a
// percentage plus Solo 1..6 / Mute 1..6 switches), so v1 songs load with
// the machine present but not its old settings.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMulti
{
    // ── MachineState ─────────────────────────────────────────────────────────
    // ReBuzz finds this property by reflection (`MachineState`), XML-serializes
    // it on save, and deserializes back on load (Build notes §14.2). The class
    // MUST be XML-serializable — public parameterless ctor, public read/write
    // members only.
    //
    // For v2.0.0 the only field is a version marker. Its real purpose here is
    // to distinguish a loaded machine from a fresh one: the setter runs iff
    // ReBuzz is restoring state, so `loaded` flips true only for saved songs.
    public class PedalGainMultiState
    {
        // Bump whenever the state schema changes so future versions can
        // reject or migrate older payloads. 2 = "v2.0.0 or later".
        public int Version { get; set; } = 2;
    }

    // ── Metering DTOs and internals ──────────────────────────────────────────

    // Snapshot returned by TakeMeters — all fields already normalised for
    // GUI display where possible; the raw sums are exposed as MeanSquare
    // (÷ (count · 32768²)) etc. so consumers don't repeat the division.
    // Slot 0 (param panel) gets no LUFS blocks or correlation; those live
    // on slots 1-3 (meter windows).
    public sealed class MeterSnapshot
    {
        public int SampleCount;

        // Per-channel, all sized to MaxTracks. Values ÷ 32768.
        public float[] PrePeak;
        public float[] PostPeak;
        // Per-channel mean square, normalised to (÷ (count · 32768²)) —
        // i.e. the value squared over 0..1 full scale.
        public float[] PreMS;
        public float[] PostMS;

        // Master true peak, ÷ 32768.
        public float TpL, TpR;

        // Master mean square (each side), normalised as above.
        public float MsL, MsR;

        // Correlation sums, ÷ count, on full-scale (÷32768) samples. Slot 0
        // leaves these zero; GUI reads them from meter-window slots.
        public float CLL, CLR, CRR;

        // Completed 100 ms K-weighted LUFS blocks that came in since the
        // last read. Slot 0 leaves this array empty; slots 1-3 populate it
        // up to a 64-entry ring buffer capacity, then increment Dropped.
        public float[] LufsBlocks;
        public int     LufsDropped;
    }

    // Persistent per-slot accumulator. Peaks track max, sums accumulate;
    // TakeMeters copies + resets under the shared lock.
    internal sealed class MeterAccum
    {
        public int Count;

        public readonly float[]  PrePeak  = new float[PedalGainMultiMachine.MaxTracks];
        public readonly float[]  PostPeak = new float[PedalGainMultiMachine.MaxTracks];
        public readonly double[] PreSq    = new double[PedalGainMultiMachine.MaxTracks];
        public readonly double[] PostSq   = new double[PedalGainMultiMachine.MaxTracks];

        public float  TpL, TpR;
        public double SqL, SqR;
        public double CLL, CLR, CRR;

        // LUFS block ring buffer (only used on slots 1-3). New blocks land
        // at LufsHead; the buffer holds up to 64 unread blocks. Overflow
        // increments LufsDropped instead of overwriting old data so the
        // GUI can tell it fell behind (matches the doc's "dropped" counter).
        public readonly float[] LufsRing = new float[64];
        public int LufsCount;
        public int LufsDropped;

        public void Reset()
        {
            Count = 0;
            for (int i = 0; i < PedalGainMultiMachine.MaxTracks; i++)
            {
                PrePeak[i] = 0f; PostPeak[i] = 0f;
                PreSq[i]   = 0.0; PostSq[i]  = 0.0;
            }
            TpL = TpR = 0f;
            SqL = SqR = 0.0;
            CLL = CLR = CRR = 0.0;
            LufsCount   = 0;
            LufsDropped = 0;
        }
    }

    // BS.1770 K-weighting biquad, transposed direct form II, double precision.
    // Coefficients are set once per SR-change; state (z1, z2) mutates per
    // sample. As a struct on a field, mutating Process(...) works correctly
    // because struct methods on field access mutate in place.
    internal struct Biquad
    {
        public double b0, b1, b2, a1, a2;
        public double z1, z2;

        public double Process(double x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }

        public void Reset() { z1 = 0.0; z2 = 0.0; }
    }

    // 4× oversampling true-peak filter: 4 phases × 12 taps, precomputed at
    // type init. Phase 0 has a single non-zero tap (h[6]=1, pure delay of 6);
    // phases 1-3 are windowed-sinc reconstructions of the intermediate points.
    // Each phase normalised so its coefficients sum to 1 (unit DC gain).
    internal static class TruePeakCoeffs
    {
        public static readonly float[][] Table = Build();

        static float[][] Build()
        {
            var t = new float[4][];
            for (int p = 0; p < 4; p++)
            {
                t[p] = new float[12];
                float f = p * 0.25f;
                double sum = 0.0;
                for (int j = 0; j < 12; j++)
                {
                    double d = 6.0 - j - f;
                    if (Math.Abs(d) >= 6.0) { t[p][j] = 0f; continue; }
                    // Normalised sinc: sin(πd)/(πd), with sinc(0)=1.
                    double sinc = (d == 0.0) ? 1.0 : Math.Sin(Math.PI * d) / (Math.PI * d);
                    // Blackman: 0.42 + 0.5·cos(πd/6) + 0.08·cos(2πd/6)
                    double w = 0.42
                             + 0.5  * Math.Cos(Math.PI * d / 6.0)
                             + 0.08 * Math.Cos(2.0 * Math.PI * d / 6.0);
                    t[p][j] = (float)(sinc * w);
                    sum += sinc * w;
                }
                if (sum > 0.0)
                    for (int j = 0; j < 12; j++)
                        t[p][j] = (float)(t[p][j] / sum);
            }
            return t;
        }
    }

    [MachineDecl(
        Name        = "Pedal Gain Multi",
        ShortName   = "PGainMul",
        Author      = "WDE",
        MaxTracks   = PedalGainMultiMachine.MaxTracks,
        // Declare the MAX shape up front. Tracker §12.2: setting a smaller
        // InputCount/OutputCount and enlarging at runtime crashes on song
        // reload if a saved connection references a channel above the initial
        // count — ReBuzz restores connections BEFORE Work() runs, so any
        // auto-sync code is unreachable. Unconnected slots stay null in the
        // Work IList and cost nothing.
        InputCount  = PedalGainMultiMachine.MaxTracks,
        OutputCount = PedalGainMultiMachine.MaxTracks + 1)]
    public class PedalGainMultiMachine : IBuzzMachine
    {
        // ── Configuration ────────────────────────────────────────────────────
        public const int MaxTracks = 24;
        public const int DefTracks = 6;   // Track count for a freshly inserted machine

        // Buzz sample convention: full-scale is ±32768. Used later (steps 2-3)
        // for meter normalisation and K-weighting; predeclared here so the
        // level table below can be trusted end-to-end.
        internal const float FullScale = 32768f;

        readonly IBuzzMachineHost host;

        // Set to true iff the MachineState setter runs. That's ReBuzz signalling
        // "I'm restoring a saved machine" — we treat its absence as "brand new".
        // NOTE: the ordering between MachineState.set and Song.MachineAdded is
        // not documented in the notes I have; if it turns out MachineState runs
        // AFTER MachineAdded in ReBuzz, a saved 1-track song could get bumped
        // to 6 tracks by the code in OnMachineAdded. Verify in step 7.
        bool loaded;

        // Live track count. Written on the UI thread (MachineAdded /
        // PropertyChanged), read on the audio thread (Work). Volatile keeps
        // the two threads honest without needing a lock — meter values in the
        // v1.2 baseline used the same pattern.
        int trackCount = 1;

        // ── Engine state (audio-thread only after first Work) ────────────────
        //
        // Per-channel ramps and glides — all sized to MaxTracks; only the
        // first `trackCount` are consulted. Out-of-range tracks are snapped
        // to defaults by ApplyChannelCounts so re-entering the range is
        // glitch-free.
        //
        // Ramps (mute, solo gate): linear, per-sample step controlled by
        // Inertia. Instant when Inertia = 0.
        //
        // Glides (L, R post-fader-pan gains; mono crossfade coefficient):
        // one-pole exponential with a hard snap threshold at 1e-4 — the
        // handoff explicitly calls out that a tighter threshold (1e-6)
        // stalls the float step just short of unity in the 1..2× gain range.
        readonly float[] muteGainCur   = new float[MaxTracks]; // 0..1
        readonly float[] soloGateCur   = new float[MaxTracks]; // 0..1
        readonly float[] glideLcur     = new float[MaxTracks]; // linear gain to L
        readonly float[] glideRcur     = new float[MaxTracks]; // linear gain to R
        readonly float[] glideMonoCur  = new float[MaxTracks]; // 0=stereo, 1=mono sum

        // Master glide (dB code → linear) and master mute ramp.
        float masterGlideCur;   // linear gain
        float masterMuteCur;    // 0..1

        // Cached SR + derived per-sample one-pole coefficient for glides.
        int   cachedSr;
        float glideK;           // 1 - exp(-1 / (0.010 · sr))  → ~10 ms glide

        // Pre-allocated scratch master buffer for when output[0] is null
        // (still need to run the master path so step-3 metering has data to
        // eat). Grows once-off if a larger block ever arrives.
        Sample[] scratchMaster = new Sample[8192];

        // First-Work snap flag. Set once on the first block after
        // construction so freshly-loaded songs don't fade-in from the
        // ctor-default engine state.
        bool engineInitialized;

        // ── Metering state ───────────────────────────────────────────────────
        //
        // Four persistent slot accumulators. Slot 0 is polled by the param
        // panel; slots 1-3 by future meter windows. Audio thread MERGES
        // per-block stats into every slot at the end of Work under one lock;
        // GUI thread SNAPSHOTS + CLEARS its slot under the same lock via
        // TakeMeters(). No lock is held during per-sample work.
        readonly MeterAccum[] slots = new MeterAccum[4]
        {
            new MeterAccum(),
            new MeterAccum(),
            new MeterAccum(),
            new MeterAccum(),
        };
        readonly object accumLock = new object();

        // ── Analysis gating ──────────────────────────────────────────────────
        //
        // Peak and RMS are nearly free (a compare and a multiply-add per
        // sample). True peak (3 phases × 12 taps × 2 sides = 72 MACs) and
        // K-weighting (4 double biquads) are NOT, and there is no point
        // running them when nothing is polling — a machine sitting in a song
        // with its parameter panel closed would otherwise pay full cost
        // forever.
        //
        // Each TakeMeters call stamps the current block counter. Work runs
        // the expensive analysis only while a read has happened recently.
        // The window is generous (~2 s at 44.1 kHz with 256-sample blocks)
        // so a 60 fps or even 1 fps poller keeps it alive continuously, but
        // a closed panel lets it switch off within a couple of seconds.
        //
        // `Volatile` on both sides: Work reads them every block on the audio
        // thread, TakeMeters writes from the UI thread.
        //
        // Sentinel 0 = "never read". Using long.MinValue here would be a bug:
        // `blk - long.MinValue` overflows to a negative value, which passes
        // the `< AnalysisHoldBlocks` test and pins the analysis permanently on.
        long blockCounter;
        long lastPeakReadBlock;      // 0 = never read
        long lastDetailReadBlock;    // 0 = never read (slots 1-3: LUFS/corr)
        const long AnalysisHoldBlocks = 350;   // ≈ 2 s at 256 samples / 44.1 kHz

        // K-weighting biquads (shelf → high-pass in series, per side).
        // Coefficients recomputed on SR change; state resets alongside them.
        Biquad shelfL, shelfR, hpL, hpR;
        int    cachedSrMeter;

        // Loudness block accumulator: spans multiple Work calls. When
        // lufsBlockSamples hits lufsBlockLen ((sr+5)/10 ≈ 100 ms), a new
        // LUFS block is completed and staged in pendingLufs for merging.
        double lufsSumL, lufsSumR;
        int    lufsBlockSamples;
        int    lufsBlockLen;

        // Completed LUFS blocks during THIS Work call. Sized to 4 which is a
        // huge safety margin — with a 4410-sample block length at 44.1 kHz
        // and typical audio buffer sizes ≤ 4096, we complete 0 or 1 block
        // per Work. Bigger buffers could complete more; overflow is
        // harmless (extras drop) but shouldn't happen in practice.
        readonly float[] pendingLufs = new float[4];
        int              pendingLufsCount;

        // True-peak filter memory: the last 12 master samples per side, held
        // as a SHIFT REGISTER rather than a ring buffer. Ring indexing needs
        // a modulo per tap (96 integer divisions per sample) which measured
        // as ~40% of total Work cost; shifting 11 floats is far cheaper and
        // keeps the tap loop's array access linear.
        float tp0L, tp1L, tp2L,  tp3L,  tp4L,  tp5L;
        float tp6L, tp7L, tp8L,  tp9L,  tp10L, tp11L;
        float tp0R, tp1R, tp2R,  tp3R,  tp4R,  tp5R;
        float tp6R, tp7R, tp8R,  tp9R,  tp10R, tp11R;

        // Per-Work scratch (reset at start of each Work). Merged into slots
        // once at end of Work under lock. Kept as fields so we don't allocate.
        readonly float[]  blockPrePeak  = new float[MaxTracks];
        readonly float[]  blockPostPeak = new float[MaxTracks];
        readonly double[] blockPreSq    = new double[MaxTracks];
        readonly double[] blockPostSq   = new double[MaxTracks];
        float             blockTpL, blockTpR;
        double            blockSqL, blockSqR;
        double            blockCLL, blockCLR, blockCRR;

        // ── MachineState property ────────────────────────────────────────────
        PedalGainMultiState machineState = new PedalGainMultiState();
        public PedalGainMultiState MachineState
        {
            get => machineState;
            set
            {
                machineState = value ?? new PedalGainMultiState();
                loaded = true;
            }
        }

        // ── Global parameters (order matters: ReBuzz uses declaration order) ─
        //
        // Declared as { get; set; } properties per Core §9 → ParameterType.Byte
        // for int params with MaxValue ≤ 254, ParameterType.Switch for bool.

        // #0 — Gain (dB code 0..181; 0 = -inf, 161 = 0 dB, 181 = +10 dB)
        int gain = UnityDbCode;
        [ParameterDecl(
            Name        = "Gain",
            Description = "Master output fader (0 = -inf, 161 = 0 dB, 181 = +10 dB)",
            MinValue    = 0,
            MaxValue    = 181,
            DefValue    = 161)]
        // Clamped on write. ManagedMachineHost.Tick range-checks before it
        // calls us, but an external control machine (Pedal Muter, PeerCtrl,
        // a pattern-driven peer) can call IParameter.SetValue directly and
        // bypass that check — see the PedalMuter notes. `gain` indexes
        // LevelTable[182], so an out-of-range value is an
        // IndexOutOfRangeException on the audio thread, not a wrong level.
        public int Gain { get => gain; set => gain = Math.Clamp(value, 0, MaxDbCode); }

        // #1 — Master Mute
        bool masterMute;
        [ParameterDecl(
            Name        = "Master Mute",
            Description = "Mute master output; fades over Inertia",
            DefValue    = 0)]
        public bool MasterMute { get => masterMute; set => masterMute = value; }

        // #2 — Inertia (ms; used for all mute + solo fades)
        int inertia = 25;
        [ParameterDecl(
            Name            = "Inertia",
            Description     = "Fade time for every mute and solo, in milliseconds (0 = instant)",
            MinValue        = 0,
            MaxValue        = 500,
            DefValue        = 25,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Inertia { get => inertia; set => inertia = Math.Clamp(value, 0, 500); }

        // ── Per-track state arrays ───────────────────────────────────────────
        // Sized to MaxTracks; only the first `trackCount` entries are ever
        // consulted by the engine. Setters bounds-check (defensive per Core
        // §14 / PedalMuter §4 — external control machines can push writes
        // beyond TrackCount).
        readonly bool[] solo   = new bool[MaxTracks];
        readonly bool[] mute   = new bool[MaxTracks];
        readonly int[]  volume = new int[MaxTracks];
        readonly int[]  pan    = new int[MaxTracks];
        readonly bool[] mono   = new bool[MaxTracks];

        // ── Core §14 recovery: sibling-track pvalue polling ──────────────────
        //
        // ReBuzz's `parametersChanged` is keyed by PARAMETER, not by
        // (parameter, track). ParameterCore.SetValue does:
        //
        //     pvalues[track] = value;
        //     machine.parametersChanged[this] = track;      // <- overwrites
        //
        // and ManagedMachineHost.Tick then walks that dictionary calling our
        // setter ONCE per parameter, for whichever track wrote last. Every
        // other track's write that tick is silently dropped.
        //
        // With a MIDI control surface this happens constantly: a bank of
        // faders or a row of mute buttons writes several tracks' Volume (or
        // Mute) inside one tick and all but one write vanishes. The visible
        // symptom is a control that "doesn't take" until you move it again.
        //
        // Every track's value IS still sitting in the parameter's pvalues
        // array when our setter runs — ClearPVal only happens at the END of
        // the tick — so we recover the dropped writes by reading pvalues
        // directly. This is the standard workaround used across this machine
        // family (Pedal Tracker, Pedal M1, Pedal Presetter, Pedal Muter).
        sealed class PolledParam
        {
            public string         Name;
            public IParameter     Param;
            public Func<int, int> Read;      // track -> raw pvalue
            public int            NoValue;
            public Action<int, int> Apply;   // (track, rawValue)
            public int            UiIndex;   // index into uiShadow / uiCooldown
        }

        PolledParam[] polled;          // null until the probe succeeds
        int           probeAttempts;

        /// True once the pvalues probe has succeeded and dropped-write
        /// recovery is live. If ReBuzz ever changes the pvalues field again
        /// (it has before: ConcurrentDictionary -> int[]) the probe fails,
        /// this stays false, and the machine degrades to the unrecovered
        /// behaviour rather than crashing — so it is worth being able to see.
        public bool PollRecoveryActive => polled != null;

        /// How many times the probe has been attempted. A large number with
        /// PollRecoveryActive false means the field could not be resolved.
        public int PollProbeAttempts => probeAttempts;

        // The pvalues array starts life as `new int[256]` — all zeros — and
        // only becomes meaningful once ReBuzz has run ClearPVal at least
        // once (which fills it with NoValue). Zero is a perfectly valid
        // value for Volume (-inf) and Pan (hard left), so polling before
        // that first clear would silently drive every untouched track to
        // silence and hard-left. We arm the poll only after observing a
        // genuine NoValue, which proves a clear has happened.
        bool pollArmed;

        // Resolve our own track parameters and a reader for their private
        // pvalues store. Retries until it succeeds — never latches an
        // "attempted" flag, because ParameterGroups are not populated until
        // after the constructor and a single early failure would otherwise
        // disable recovery for the lifetime of the machine.
        void TryProbePValues()
        {
            if (polled != null) return;
            probeAttempts++;

            try
            {
                var groups = host?.Machine?.ParameterGroups;
                if (groups == null) return;

                IParameterGroup trackGroup = null;
                foreach (var g in groups)
                    if (g != null && g.Type == ParameterGroupType.Track) { trackGroup = g; break; }
                if (trackGroup?.Parameters == null) return;

                var list = new List<PolledParam>(5);
                foreach (var p in trackGroup.Parameters)
                {
                    if (p == null) continue;
                    var reader = MakePValueReader(p);
                    if (reader == null) return;        // probe failed — retry next time

                    Action<int, int> apply;
                    int ui;
                    switch (p.Name)
                    {
                        case "Solo":   apply = (t, v) => solo  [t] = v != 0;                      ui = UiSolo;   break;
                        case "Mute":   apply = (t, v) => mute  [t] = v != 0;                      ui = UiMute;   break;
                        case "Mono":   apply = (t, v) => mono  [t] = v != 0;                      ui = UiMono;   break;
                        case "Volume": apply = (t, v) => volume[t] = Math.Clamp(v, 0, MaxDbCode); ui = UiVolume; break;
                        case "Pan":    apply = (t, v) => pan   [t] = Math.Clamp(v, 0, MaxPanCode);ui = UiPan;    break;
                        default: continue;
                    }

                    list.Add(new PolledParam {
                        Name = p.Name, Param = p, Read = reader,
                        NoValue = p.NoValue, Apply = apply, UiIndex = ui
                    });
                }

                if (list.Count == 0) return;
                polled = list.ToArray();                // commit only on full success
            }
            catch { /* transient — retry on the next setter call */ }
        }

        // ParameterCore.pvalues has changed representation across ReBuzz
        // versions: ConcurrentDictionary<int,int> on older builds, int[256]
        // on current ones. An `as` cast to the wrong one returns null
        // silently and disables recovery with no error, so detect the shape
        // once and close over whichever is present.
        static Func<int, int> MakePValueReader(IParameter p)
        {
            try
            {
                var fi = p.GetType().GetField("pvalues",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (fi == null) return null;

                object raw = fi.GetValue(p);
                if (raw == null) return null;

                if (raw is int[] arr)
                    return t => (uint)t < (uint)arr.Length ? arr[t] : p.NoValue;

                if (raw is System.Collections.Concurrent.ConcurrentDictionary<int, int> dict)
                    return t => dict.TryGetValue(t, out int v) ? v : p.NoValue;

                return null;
            }
            catch { return null; }
        }

        // Called from every track-parameter setter. Re-applies any track's
        // value that is sitting in pvalues this tick, which recovers the
        // writes `parametersChanged` dropped. Idempotent: the firing track's
        // own pvalue is exactly the value the setter was just handed, so
        // re-applying it is a no-op.
        void RecoverDroppedTrackWrites()
        {
            if (polled == null) TryProbePValues();
            var ps = polled;
            if (ps == null) return;

            int n = Volatile.Read(ref trackCount);
            if (n < 1) n = 1;
            if (n > MaxTracks) n = MaxTracks;

            // Pass 1 — arm. Seeing a single NoValue anywhere proves ReBuzz has
            // run ClearPVal on this machine, so the array holds real data
            // rather than its initial all-zero state. This has to be a
            // separate pass: arming inside the apply loop would skip every
            // track examined before the first NoValue turned up.
            if (!pollArmed)
            {
                for (int i = 0; i < ps.Length && !pollArmed; i++)
                {
                    var pp = ps[i];
                    for (int t = 0; t < n; t++)
                        if (pp.Read(t) == pp.NoValue) { pollArmed = true; break; }
                }
                if (!pollArmed) return;
            }

            // Pass 2 — apply every value present this tick. Idempotent: the
            // firing track's own pvalue is the value the setter was just
            // handed, so re-applying it changes nothing.
            for (int i = 0; i < ps.Length; i++)
            {
                var pp = ps[i];
                for (int t = 0; t < n; t++)
                {
                    int v = pp.Read(t);
                    if (v != pp.NoValue) pp.Apply(t, v);
                }
            }
        }

        // ── ReBuzz parameter-window UI sync ──────────────────────────────────
        //
        // The same `parametersChanged` bottleneck that dropped our setter
        // calls also starves ReBuzz's OWN parameter sliders. MachineWorkInstance
        // calls `par.InvokeEvents(buzz, track)` from the same one-entry-per-
        // parameter dictionary, so only the surviving track's ParameterVM gets
        // a valueChanged event — and ParameterVM.Value is read lazily
        // (`parameter.GetValue(track)`), repainting only when that event
        // arrives. The underlying value is correct; the slider is just stale.
        //
        // Our own panel never shows this because RefreshControlsFromParameters
        // polls GetValue every frame. ReBuzz's generic slider list does not.
        //
        // We can detect it exactly rather than by guesswork. The host calls
        // our setter and InvokeEvents from the SAME dictionary entry, so:
        //
        //     "our setter ran for (param, track)"
        //         <=> "the host raised the UI event for (param, track)"
        //
        // So every setter call records the value the UI has therefore been
        // told about. Any track where GetValue has since moved away from that
        // shadow is a track whose event was dropped, and we nudge it: a
        // value-preserving re-write of the value the parameter already holds,
        // whose only effect is to make the host raise the event next tick.
        //
        // The nudge sets the parameter to `GetValue(track)` — not to our own
        // cached copy — so it cannot clobber a fresher write by construction,
        // and it carries ReBuzz's do-not-record flag so it never leaks into
        // pattern recording.
        const int UiSolo = 0, UiMute = 1, UiVolume = 2, UiPan = 3, UiMono = 4;
        const int UiParamCount = 5;

        // ParameterCore.SetValue: `track & (1 << 16)` suppresses
        // RecordControlChange, so a nudge never writes pattern data.
        const int NoRecordFlag = 1 << 16;

        // Frames to wait before reconsidering a track we have just nudged.
        // One nudge per parameter per frame is the most that can ever land —
        // two in the same tick would collide in the very dictionary slot we
        // are working around — and the cooldown stops a nudge that gets
        // displaced from being re-issued every single frame.
        const int UiNudgeCooldown = 4;

        readonly int[,] uiShadow   = new int[UiParamCount, MaxTracks];
        readonly int[,] uiCooldown = new int[UiParamCount, MaxTracks];
        bool uiBaselined;
        int  uiNudgeCount;

        /// Total UI nudges issued. Should sit at zero on a single-track song
        /// and climb only when several tracks of one parameter move together.
        public int UiNudgeCount => uiNudgeCount;

        // Called from every track setter. Reaching here proves the host
        // dispatched this (parameter, track), and therefore also raised its
        // valueChanged event, so the parameter window is up to date for it.
        void NoteUiDelivered(int ui, int track, int rawValue)
        {
            if ((uint)track < MaxTracks) uiShadow[ui, track] = rawValue;
        }

        /// Repaint any parameter-window slider the host forgot to notify.
        /// Call from the GUI timer; returns the number of nudges issued.
        /// Deliberately NOT called from the audio path — this is a view
        /// problem, and with no view open there is nothing to repair.
        public int SyncParameterUi()
        {
            if (polled == null) TryProbePValues();
            var ps = polled;
            if (ps == null) return 0;

            int n = Volatile.Read(ref trackCount);
            if (n < 1) n = 1;
            if (n > MaxTracks) n = MaxTracks;

            try
            {
                // First call: the window has just bound its ParameterVMs and
                // read GetValue for each, so the view is in sync right now.
                // Baselining here rather than at construction avoids a burst
                // of pointless nudges from SetParameterDefaults, which writes
                // every parameter on every track at machine creation.
                if (!uiBaselined)
                {
                    for (int i = 0; i < ps.Length; i++)
                        for (int t = 0; t < MaxTracks; t++)
                            uiShadow[ps[i].UiIndex, t] = ps[i].Param.GetValue(t);
                    uiBaselined = true;
                    return 0;
                }

                int nudged = 0;
                for (int i = 0; i < ps.Length; i++)
                {
                    var pp = ps[i];
                    int ui = pp.UiIndex;
                    int target = -1, targetValue = 0;

                    for (int t = 0; t < n; t++)
                    {
                        if (uiCooldown[ui, t] > 0) { uiCooldown[ui, t]--; continue; }
                        if (target >= 0) continue;

                        int cur = pp.Param.GetValue(t);
                        if (cur != uiShadow[ui, t]) { target = t; targetValue = cur; }
                    }

                    if (target < 0) continue;

                    // Value-preserving by construction: we write back exactly
                    // what the parameter already holds. The shadow is NOT
                    // updated here — only the setter callback may do that, so
                    // a nudge that loses the dictionary slot to a real write
                    // is simply retried once its cooldown expires.
                    pp.Param.SetValue(target | NoRecordFlag, targetValue);
                    uiCooldown[ui, target] = UiNudgeCooldown;
                    nudged++;
                    uiNudgeCount++;
                }
                return nudged;
            }
            catch { return 0; }
        }

        // ── Precomputed dB code → linear gain table ──────────────────────────
        // g[0] = 0 (mute); g[c] = 10^((c - 161) · 0.5 / 20) for c in 1..181.
        // Used by DescribeValue for display and — later — by the engine as the
        // per-block target gain lookup. Built once at type-init.
        public const int MaxDbCode  = 181;   // 0 = -inf, 161 = 0 dB, 181 = +10 dB
        public const int UnityDbCode = 161;
        public const int MaxPanCode = 128;   // 0 = L, 64 = C, 128 = R
        public const int CentrePan  = 64;

        internal static readonly float[] LevelTable = BuildLevelTable();
        static float[] BuildLevelTable()
        {
            var t = new float[MaxDbCode + 1];
            t[0] = 0f;
            for (int c = 1; c <= MaxDbCode; c++)
                t[c] = (float)Math.Pow(10.0, (c - UnityDbCode) * 0.5 / 20.0);
            return t;
        }

        // ── Construction ─────────────────────────────────────────────────────
        public PedalGainMultiMachine(IBuzzMachineHost host)
        {
            this.host = host;

            // Track defaults — mirror the ParameterDecl DefValues so a Work
            // call sees sensible values even before ReBuzz's parameter setters
            // have run for every track.
            for (int t = 0; t < MaxTracks; t++)
            {
                volume[t] = UnityDbCode;   // 0 dB
                pan[t]    = CentrePan;     // Centre
            }

            // Channel counts and the machine's own IMachine ref only become
            // available after ReBuzz has finished constructing us and added us
            // to the graph. Wire up via Song events (Build notes §8.1).
            Global.Buzz.Song.MachineAdded   += OnMachineAdded;
            Global.Buzz.Song.MachineRemoved += OnMachineRemoved;
        }

        // ── Track parameters (order matters: after every global) ─────────────
        //
        // Track parameter shape: `public void Method(T value, int track)`.
        // ReBuzz infers the parameter type from `T`; the method name is C#
        // only, the user-visible name comes from ParameterDecl.Name.

        // #3 — Solo (per-track)
        [ParameterDecl(
            Name        = "Solo",
            Description = "Solo this track. Takes effect on the master mix only, not on the direct out.",
            DefValue    = 0)]
        public void SetSolo(bool v, int track)
        {
            if ((uint)track < MaxTracks) solo[track] = v;
            NoteUiDelivered(UiSolo, track, v ? 1 : 0);
            RecoverDroppedTrackWrites();
        }

        // #4 — Mute (per-track)
        [ParameterDecl(
            Name        = "Mute",
            Description = "Mute this track. Applies to the master mix AND the direct out; fades over Inertia.",
            DefValue    = 0)]
        public void SetMute(bool v, int track)
        {
            if ((uint)track < MaxTracks) mute[track] = v;
            NoteUiDelivered(UiMute, track, v ? 1 : 0);
            RecoverDroppedTrackWrites();
        }

        // #5 — Volume (per-track, dB code 0..181)
        [ParameterDecl(
            Name        = "Volume",
            Description = "Track fader (0 = -inf, 161 = 0 dB, 181 = +10 dB)",
            MinValue    = 0,
            MaxValue    = 181,
            DefValue    = 161)]
        public void SetVolume(int v, int track)
        {
            if ((uint)track < MaxTracks) volume[track] = Math.Clamp(v, 0, MaxDbCode);
            NoteUiDelivered(UiVolume, track, v);
            RecoverDroppedTrackWrites();
        }

        // #6 — Pan (per-track, 0=L, 64=C, 128=R)
        [ParameterDecl(
            Name        = "Pan",
            Description = "Pan (0 = full left, 64 = centre, 128 = full right). Balance law on stereo channels, constant-power on mono.",
            MinValue    = 0,
            MaxValue    = 128,
            DefValue    = 64)]
        public void SetPan(int v, int track)
        {
            if ((uint)track < MaxTracks) pan[track] = Math.Clamp(v, 0, MaxPanCode);
            NoteUiDelivered(UiPan, track, v);
            RecoverDroppedTrackWrites();
        }

        // #7 — Mono (per-track)
        [ParameterDecl(
            Name        = "Mono",
            Description = "Sum L+R to mono before the fader and pan",
            DefValue    = 0)]
        public void SetMono(bool v, int track)
        {
            if ((uint)track < MaxTracks) mono[track] = v;
            NoteUiDelivered(UiMono, track, v ? 1 : 0);
            RecoverDroppedTrackWrites();
        }

        // ── Song event handlers ──────────────────────────────────────────────

        void OnMachineAdded(IMachine m)
        {
            if (m != host.Machine) return;

            // Watch for track-count changes triggered by the user (add/remove
            // track via ReBuzz's UI, our own future `− / +` buttons, or a
            // future pattern-driven change).
            m.PropertyChanged += OnMachinePropertyChanged;

            // For a freshly inserted machine, bump TrackCount to DefTracks.
            // A machine restored from a saved song already has its saved
            // TrackCount at this point (ReBuzz restores it before firing
            // MachineAdded), so we only touch new machines.
            //
            // The `loaded` guard uses MachineState presence; if it turns out
            // that setter runs AFTER MachineAdded in ReBuzz, this could
            // incorrectly bump a saved 1-track song to 6. Verify at step 7.
            if (!loaded && m.TrackCount < DefTracks)
                m.TrackCount = DefTracks;

            ApplyChannelCounts();
        }

        void OnMachineRemoved(IMachine m)
        {
            if (m != host.Machine) return;
            m.PropertyChanged                -= OnMachinePropertyChanged;
            Global.Buzz.Song.MachineAdded    -= OnMachineAdded;
            Global.Buzz.Song.MachineRemoved  -= OnMachineRemoved;
        }

        void OnMachinePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Only care about TrackCount here. Other property changes are
            // handled by ReBuzz itself.
            if (e.PropertyName != "TrackCount") return;
            ApplyChannelCounts();
        }

        // Reconcile our per-track state and the host's channel counts with the
        // machine's current TrackCount. UI thread only (both callers are on it).
        void ApplyChannelCounts()
        {
            int n = Math.Clamp(host.Machine.TrackCount, 1, MaxTracks);

            int newIn  = n;
            int newOut = n + 1;   // channel 0 = master; channels 1..n = direct outs

            int cur = Volatile.Read(ref trackCount);

            // Ordering matters for glitch-free resize (handoff doc §Parameters):
            //   • Shrinking → lower this machine's count FIRST so Work() only
            //     ever indexes into a range the host arrays still cover.
            //   • Growing  → raise the host counts FIRST so the extra slots
            //     exist before Work() starts writing to them.
            if (n < cur)
            {
                Volatile.Write(ref trackCount, n);
                host.InputChannelCount  = newIn;
                host.OutputChannelCount = newOut;
            }
            else
            {
                host.InputChannelCount  = newIn;
                host.OutputChannelCount = newOut;
                Volatile.Write(ref trackCount, n);
            }

            // Removed tracks: reset their per-track state to defaults so a
            // later re-add lands on a clean slate instead of stale values.
            // Engine ramps and glides are snapped to the same defaults —
            // when the track re-enters the count, its state matches its
            // (default) parameters and the first-block ramp/glide is a no-op.
            for (int t = n; t < MaxTracks; t++)
            {
                solo[t]   = false;
                mute[t]   = false;
                mono[t]   = false;
                volume[t] = UnityDbCode;
                pan[t]    = CentrePan;

                muteGainCur[t]  = 1f;
                soloGateCur[t]  = 1f;
                glideLcur[t]    = 1f;   // vol=161 → linear 1, pan=64 balance → (1, 1)
                glideRcur[t]    = 1f;
                glideMonoCur[t] = 0f;
            }
        }

        // ── Channel naming ───────────────────────────────────────────────────
        // 0-based labels per the handoff:
        //   Inputs   : "In 0", "In 1", ...
        //   Outputs  : "Master" for output 0, "Direct In 0" for output 1, ...
        //     (output k+1 carries the dry signal for track k)
        public string GetChannelName(bool input, int index)
        {
            if (input)
            {
                if (index >= 0 && index < MaxTracks)
                    return "In " + index;
                return null;
            }

            if (index == 0)                       return "Master";
            if (index >= 1 && index <= MaxTracks) return "Direct In " + (index - 1);
            return null;
        }

        // ── DescribeValue ────────────────────────────────────────────────────
        // Called by ReBuzz on the UI thread for hover-tooltips in the
        // parameter view and pattern editor. Returning null falls back to
        // ReBuzz's default rendering (raw integer, or the ValueDescriptor
        // formatter — e.g. `Descriptors.Milliseconds` for Inertia).
        public string DescribeValue(IParameter param, int value)
        {
            if (param == null) return null;

            switch (param.Name)
            {
                case "Gain":
                case "Volume":
                    return FormatDbCode(value);

                case "Inertia":
                    return value == 0 ? "0 ms (instant)" : value + " ms";

                case "Pan":
                    if (value == 64) return "C";
                    if (value <  64) return "L " + ((64 - value) * 100 / 64) + "%";
                    return              "R " + ((value - 64) * 100 / 64) + "%";
            }
            return null;
        }

        // dB code convention (from the handoff):
        //   0        → -inf dB (mute)
        //   c in 1..181 → (c - 161) · 0.5 dB
        //     1   → -80 dB
        //     149 → -6 dB
        //     161 → 0 dB
        //     181 → +10 dB
        static string FormatDbCode(int code)
        {
            if (code <= 0) return "-inf dB";
            float db = (code - 161) * 0.5f;
            string s = db.ToString("0.0", CultureInfo.InvariantCulture);
            return (db >= 0f ? "+" : "") + s + " dB";
        }

        // ── Pan laws ─────────────────────────────────────────────────────────
        // Balance (used on stereo channels):
        //   pan ≤ 64 → l = 1,           r = pan / 64
        //   pan > 64 → l = (128−pan)/64, r = 1
        static void Balance(int pan, out float bl, out float br)
        {
            if (pan <= 64) { bl = 1f;                      br = pan  * (1f / 64f); }
            else           { bl = (128 - pan) * (1f / 64f); br = 1f;                }
        }

        // Constant power (used on mono channels):
        //   θ = pan / 128 · π/2 ; l = cos θ, r = sin θ
        // Endpoints special-cased for exactness — MathF.Cos(π/2) leaves a
        // ~4e-8 residual that the handoff flags as "exact (0,1) at 128".
        static void ConstantPowerPan(int pan, out float bl, out float br)
        {
            if (pan == 0)   { bl = 1f; br = 0f; return; }
            if (pan == 128) { bl = 0f; br = 1f; return; }
            float theta = pan * (MathF.PI * 0.5f / 128f);
            bl = MathF.Cos(theta);
            br = MathF.Sin(theta);
        }

        // ── Metering helpers ─────────────────────────────────────────────────

        // (Re)compute K-weighting coefficients and the 100 ms block length
        // for the current sample rate. Called from Work when SR changes.
        // The formulas are BS.1770 verbatim — at 48 kHz they must match the
        // reference table to 1e-9 (verified in the sandbox).
        void EnsureMeteringForSampleRate(int sr)
        {
            if (sr == cachedSrMeter || sr <= 0) return;
            cachedSrMeter = sr;

            double fs = sr;

            // Shelf: high shelf at ~1682 Hz, +3.999844 dB, Q ≈ 0.707175
            const double f0s = 1681.974450955533;
            const double Gs  = 3.999843853973347;
            const double Qs  = 0.7071752369554196;
            double Ks  = Math.Tan(Math.PI * f0s / fs);
            double Vh  = Math.Pow(10.0, Gs / 20.0);
            double Vb  = Math.Pow(Vh, 0.4996667741545416);
            double a0s = 1.0 + Ks / Qs + Ks * Ks;

            shelfL.b0 = (Vh + Vb * Ks / Qs + Ks * Ks) / a0s;
            shelfL.b1 = 2.0 * (Ks * Ks - Vh) / a0s;
            shelfL.b2 = (Vh - Vb * Ks / Qs + Ks * Ks) / a0s;
            shelfL.a1 = 2.0 * (Ks * Ks - 1.0) / a0s;
            shelfL.a2 = (1.0 - Ks / Qs + Ks * Ks) / a0s;
            shelfR = shelfL;

            // High-pass: 38.135 Hz, Q ≈ 0.500327; b = (1, -2, 1)/a0
            const double f0h = 38.13547087602444;
            const double Qh  = 0.5003270373238773;
            double Kh  = Math.Tan(Math.PI * f0h / fs);
            double a0h = 1.0 + Kh / Qh + Kh * Kh;

            hpL.b0 =  1.0 / a0h;
            hpL.b1 = -2.0 / a0h;
            hpL.b2 =  1.0 / a0h;
            hpL.a1 =  2.0 * (Kh * Kh - 1.0) / a0h;
            hpL.a2 = (1.0 - Kh / Qh + Kh * Kh) / a0h;
            hpR = hpL;

            // Reset biquad state so the transition doesn't leak old samples.
            shelfL.Reset(); shelfR.Reset(); hpL.Reset(); hpR.Reset();

            // 100 ms block length; the (+5) rounds to nearest.
            lufsBlockLen     = (sr + 5) / 10;
            lufsSumL         = 0.0;
            lufsSumR         = 0.0;
            lufsBlockSamples = 0;

            // Reset true-peak memory too — old samples are meaningless at a
            // different rate.
            tp0L = tp1L = tp2L = tp3L = tp4L  = tp5L  = 0f;
            tp6L = tp7L = tp8L = tp9L = tp10L = tp11L = 0f;
            tp0R = tp1R = tp2R = tp3R = tp4R  = tp5R  = 0f;
            tp6R = tp7R = tp8R = tp9R = tp10R = tp11R = 0f;
        }

        // Snapshot + clear one slot. Called from the UI thread; blocks the
        // audio thread only for the duration of the copy. The GUI does the
        // final normalisation (÷ count, ÷ 32768²) at display time.
        //
        //   slot 0 — param panel (peaks + RMS only; no LUFS or correlation)
        //   slot 1-3 — meter windows (everything)
        public MeterSnapshot TakeMeters(int slot)
        {
            if ((uint)slot >= 4) return null;

            // Mark that someone is polling, so Work keeps the expensive
            // analysis switched on. Slots 1-3 additionally want true peak,
            // LUFS and correlation; slot 0 (param panel) only needs the
            // cheap peak/RMS path.
            //
            // Stamping the CURRENT block means analysis is live from the very
            // next Work call — a freshly-opened panel loses no more than the
            // one stale frame the handoff already tells us to discard.
            long now = Volatile.Read(ref blockCounter);
            if (now == 0) now = 1;   // never-read sentinel is 0; don't write it back
            Volatile.Write(ref lastPeakReadBlock, now);
            if (slot >= 1) Volatile.Write(ref lastDetailReadBlock, now);

            var snap = new MeterSnapshot
            {
                PrePeak  = new float[MaxTracks],
                PostPeak = new float[MaxTracks],
                PreMS    = new float[MaxTracks],
                PostMS   = new float[MaxTracks],
            };

            var s = slots[slot];
            lock (accumLock)
            {
                int count = s.Count;
                snap.SampleCount = count;
                float invFullSq = count > 0
                    ? 1f / (count * PedalGainMultiMachine.FullScale * PedalGainMultiMachine.FullScale)
                    : 0f;
                float invCount = count > 0 ? 1f / count : 0f;

                for (int t = 0; t < MaxTracks; t++)
                {
                    snap.PrePeak [t] = s.PrePeak [t] * (1f / PedalGainMultiMachine.FullScale);
                    snap.PostPeak[t] = s.PostPeak[t] * (1f / PedalGainMultiMachine.FullScale);
                    snap.PreMS   [t] = (float)(s.PreSq [t] * invFullSq);
                    snap.PostMS  [t] = (float)(s.PostSq[t] * invFullSq);
                }
                snap.TpL = s.TpL * (1f / PedalGainMultiMachine.FullScale);
                snap.TpR = s.TpR * (1f / PedalGainMultiMachine.FullScale);
                snap.MsL = (float)(s.SqL * invFullSq);
                snap.MsR = (float)(s.SqR * invFullSq);
                snap.CLL = (float)(s.CLL * invCount);
                snap.CLR = (float)(s.CLR * invCount);
                snap.CRR = (float)(s.CRR * invCount);

                if (slot >= 1 && s.LufsCount > 0)
                {
                    snap.LufsBlocks = new float[s.LufsCount];
                    Array.Copy(s.LufsRing, 0, snap.LufsBlocks, 0, s.LufsCount);
                }
                else
                {
                    snap.LufsBlocks = Array.Empty<float>();
                }
                snap.LufsDropped = s.LufsDropped;

                s.Reset();
            }
            return snap;
        }

        // Reset all per-Work block accumulators. Called at the top of Work.
        void ResetBlockAccumulators()
        {
            for (int t = 0; t < MaxTracks; t++)
            {
                blockPrePeak [t] = 0f;
                blockPostPeak[t] = 0f;
                blockPreSq   [t] = 0.0;
                blockPostSq  [t] = 0.0;
            }
            blockTpL = 0f; blockTpR = 0f;
            blockSqL = 0.0; blockSqR = 0.0;
            blockCLL = 0.0; blockCLR = 0.0; blockCRR = 0.0;
            pendingLufsCount = 0;
        }

        // Fold the per-Work stats into every slot. Under lock; runs once per
        // Work call, so contention with GUI polling (60 fps) is minimal.
        void MergeBlockIntoSlots(int n)
        {
            lock (accumLock)
            {
                for (int i = 0; i < 4; i++)
                {
                    var s = slots[i];
                    s.Count += n;
                    for (int t = 0; t < MaxTracks; t++)
                    {
                        if (blockPrePeak [t] > s.PrePeak [t]) s.PrePeak [t] = blockPrePeak [t];
                        if (blockPostPeak[t] > s.PostPeak[t]) s.PostPeak[t] = blockPostPeak[t];
                        s.PreSq [t] += blockPreSq [t];
                        s.PostSq[t] += blockPostSq[t];
                    }
                    if (blockTpL > s.TpL) s.TpL = blockTpL;
                    if (blockTpR > s.TpR) s.TpR = blockTpR;
                    s.SqL += blockSqL;
                    s.SqR += blockSqR;

                    // Correlation + LUFS blocks are meter-window only; slot 0
                    // stays untouched for them.
                    if (i >= 1)
                    {
                        s.CLL += blockCLL;
                        s.CLR += blockCLR;
                        s.CRR += blockCRR;
                        for (int k = 0; k < pendingLufsCount; k++)
                        {
                            if (s.LufsCount < s.LufsRing.Length)
                                s.LufsRing[s.LufsCount++] = pendingLufs[k];
                            else
                                s.LufsDropped++;
                        }
                    }
                }
            }
        }


        //
        // Signal flow per the handoff §Audio engine, one channel strip:
        //
        //   input[t]  ──►  Mono ──►  Mute ──►  Volume/Pan ─┬─► direct[t+1]
        //                                                  │
        //                                                  └─► × Solo ──► master sum
        //
        //   master sum ──►  Gain ──►  Master Mute ──►  output[0]
        //
        // Direct outs branch off after Volume/Pan and BEFORE Solo, Gain, and
        // Master Mute — those never touch the direct out (handoff's diagram).
        //
        // Ramps + glides live in the same per-sample loop so there's one
        // multiply per sample per stage rather than several passes over the
        // block.
        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (mode == WorkModes.WM_NOIO || output == null) return false;

            // Snapshot the track count once for this block.
            int nT = Volatile.Read(ref trackCount);
            if (nT < 1) nT = 1;
            if (nT > MaxTracks) nT = MaxTracks;

            // Sample rate + glide coefficient. Recompute glideK only when SR
            // changes (typically once, at load time).
            int sr = host.MasterInfo.SamplesPerSec;
            if (sr > 0 && sr != cachedSr)
            {
                cachedSr = sr;
                glideK   = 1f - MathF.Exp(-1f / (0.010f * sr));
            }
            EnsureMeteringForSampleRate(sr);

            // Reset per-Work block accumulators. Merged into slots under
            // lock at the end of Work.
            ResetBlockAccumulators();

            // Grow scratch master if a bigger block ever arrives. This happens
            // at most once per session in practice; ReBuzz's block size is
            // stable within a session.
            if (n > scratchMaster.Length) scratchMaster = new Sample[n];

            // Master output buffer. When nothing is wired to output 0, feed
            // the scratch buffer instead — step-3 metering must still run
            // over the summed master, otherwise LUFS/true-peak freeze whenever
            // the user unplugs the master.
            int outCount = output.Count;
            Sample[] master = (outCount > 0 && output[0] != null) ? output[0] : scratchMaster;

            // Zero the master (or the tail of the scratch if we shrunk).
            for (int s = 0; s < n; s++) { master[s].L = 0f; master[s].R = 0f; }

            // Solo evaluation is per-block: a channel is heard on the master
            // iff (no solo is active) OR (this channel is soloed).
            bool anySolo = false;
            for (int t = 0; t < nT; t++) if (solo[t]) { anySolo = true; break; }

            // Ramp step for mute and solo (linear). Inertia=0 (or SR not yet
            // known) → step of 1.0 = instant snap in one sample.
            float fadeSec  = inertia * 0.001f;
            float rampStep = (fadeSec > 0f && cachedSr > 0)
                ? 1f / (cachedSr * fadeSec)
                : 1f;

            // Master targets — computed once per block; the per-sample loop
            // glides toward them.
            float tgtMasterGain = LevelTable[gain];
            float tgtMasterMute = masterMute ? 0f : 1f;

            // First-block snap so a freshly-loaded song doesn't fade-in from
            // whatever ctor-defaults the engine had. Also handles a fresh
            // insertion (ctor values happen to match, but this is one branch,
            // not two).
            if (!engineInitialized)
            {
                engineInitialized = true;
                masterGlideCur    = tgtMasterGain;
                masterMuteCur     = tgtMasterMute;

                for (int t = 0; t < MaxTracks; t++)
                {
                    muteGainCur[t] = mute[t] ? 0f : 1f;
                    // Solo gate default: closed for channels excluded by an
                    // active solo, open otherwise.
                    soloGateCur[t] = (anySolo && !solo[t]) ? 0f : 1f;

                    // Snap glides to per-channel targets.
                    float v = LevelTable[volume[t]];
                    float bl, br;
                    if (mono[t]) ConstantPowerPan(pan[t], out bl, out br);
                    else         Balance         (pan[t], out bl, out br);
                    glideLcur[t]    = v * bl;
                    glideRcur[t]    = v * br;
                    glideMonoCur[t] = mono[t] ? 1f : 0f;
                }
            }

            // Bump the block counter and decide how much analysis to run.
            // Nothing polling → skip the meter taps entirely.
            long blk = Volatile.Read(ref blockCounter) + 1;
            Volatile.Write(ref blockCounter, blk);
            long lastPeak   = Volatile.Read(ref lastPeakReadBlock);
            long lastDetail = Volatile.Read(ref lastDetailReadBlock);
            bool wantAny    = lastPeak   != 0 && (blk - lastPeak)   < AnalysisHoldBlocks;
            bool wantDetail = lastDetail != 0 && (blk - lastDetail) < AnalysisHoldBlocks;

            // Per-channel strip processing.
            int inCount = input?.Count ?? 0;
            bool anyInput = false;

            for (int t = 0; t < nT; t++)
            {
                Sample[] inBuf  = (t < inCount)         ? input [t]     : null;
                Sample[] direct = (t + 1 < outCount)    ? output[t + 1] : null;

                // Per-channel targets — one lookup + one branch per block.
                float v = LevelTable[volume[t]];
                float bl, br;
                if (mono[t]) ConstantPowerPan(pan[t], out bl, out br);
                else         Balance         (pan[t], out bl, out br);
                float tgtL    = v * bl;
                float tgtR    = v * br;
                float tgtMono = mono[t] ? 1f : 0f;
                float tgtMute = mute[t] ? 0f : 1f;
                float tgtSolo = (anySolo && !solo[t]) ? 0f : 1f;

                // Local aliases so the inner loop touches locals, not fields.
                float mGain = muteGainCur [t];
                float sGate = soloGateCur [t];
                float gL    = glideLcur   [t];
                float gR    = glideRcur   [t];
                float gM    = glideMonoCur[t];

                if (inBuf == null)
                {
                    // Disconnected input: park every ramp and glide at its
                    // target so the moment audio arrives it's already at the
                    // right level (no fade-in on reconnect). Direct out gets
                    // zeroed too, otherwise stale audio would leak.
                    muteGainCur [t] = tgtMute;
                    soloGateCur [t] = tgtSolo;
                    glideLcur   [t] = tgtL;
                    glideRcur   [t] = tgtR;
                    glideMonoCur[t] = tgtMono;

                    if (direct != null)
                        for (int s = 0; s < n; s++) { direct[s].L = 0f; direct[s].R = 0f; }
                    continue;
                }

                anyInput = true;

                float chPrePeak  = 0f, chPostPeak = 0f;
                double chPreSq   = 0.0, chPostSq  = 0.0;

                for (int s = 0; s < n; s++)
                {
                    // Mute ramp — linear.
                    if      (mGain < tgtMute) mGain = MathF.Min(mGain + rampStep, tgtMute);
                    else if (mGain > tgtMute) mGain = MathF.Max(mGain - rampStep, tgtMute);

                    // Solo gate ramp — linear.
                    if      (sGate < tgtSolo) sGate = MathF.Min(sGate + rampStep, tgtSolo);
                    else if (sGate > tgtSolo) sGate = MathF.Max(sGate - rampStep, tgtSolo);

                    // Glides — one-pole exponential with 1e-4 snap threshold.
                    gL += (tgtL    - gL) * glideK;
                    gR += (tgtR    - gR) * glideK;
                    gM += (tgtMono - gM) * glideK;
                    if (MathF.Abs(tgtL    - gL) < 1e-4f) gL = tgtL;
                    if (MathF.Abs(tgtR    - gR) < 1e-4f) gR = tgtR;
                    if (MathF.Abs(tgtMono - gM) < 1e-4f) gM = tgtMono;

                    float l = inBuf[s].L;
                    float r = inBuf[s].R;

                    // Pre-strip meter tap (raw input into this channel).
                    if (wantAny)
                    {
                        float absL = MathF.Abs(l), absR = MathF.Abs(r);
                        if (absL > chPrePeak) chPrePeak = absL;
                        if (absR > chPrePeak) chPrePeak = absR;
                        chPreSq += (l * (double)l + r * (double)r) * 0.5;
                    }

                    // Mono crossfade: linear blend from stereo to (L+R)/2 both
                    // sides. When gM=0 → ml=l, mr=r (pure stereo).
                    //                gM=1 → ml=mr=(l+r)/2 (pure mono sum).
                    float avg = (l + r) * 0.5f;
                    float ml  = l + (avg - l) * gM;
                    float mr  = r + (avg - r) * gM;

                    // Volume + pan + mute.
                    float outL = ml * mGain * gL;
                    float outR = mr * mGain * gR;

                    // Post-strip meter tap (after Mono/Mute/Vol/Pan, before
                    // solo gate — same convention the handoff calls out).
                    if (wantAny)
                    {
                        float absOL = MathF.Abs(outL), absOR = MathF.Abs(outR);
                        if (absOL > chPostPeak) chPostPeak = absOL;
                        if (absOR > chPostPeak) chPostPeak = absOR;
                        chPostSq += (outL * (double)outL + outR * (double)outR) * 0.5;
                    }

                    // Direct out — post-mute/vol/pan, pre-solo. Gain and
                    // Master Mute NEVER reach this line.
                    if (direct != null) { direct[s].L = outL; direct[s].R = outR; }

                    // Master gets it through the solo gate.
                    master[s].L += outL * sGate;
                    master[s].R += outR * sGate;
                }

                muteGainCur [t] = mGain;
                soloGateCur [t] = sGate;
                glideLcur   [t] = gL;
                glideRcur   [t] = gR;
                glideMonoCur[t] = gM;

                // Fold this channel's per-sample stats into the block accums.
                blockPrePeak [t] = chPrePeak;
                blockPostPeak[t] = chPostPeak;
                blockPreSq   [t] = chPreSq;
                blockPostSq  [t] = chPostSq;
            }

            // Master stage — Gain glide + Master Mute ramp + all master
            // meter taps in one per-sample loop. Runs even when output[0]
            // is null (into scratch) so metering keeps flowing whenever
            // the master is unwired downstream.
            float  mMute    = masterMuteCur;
            float  mGainCur = masterGlideCur;
            float  tpL_local = blockTpL, tpR_local = blockTpR;
            double sqL_local = 0.0, sqR_local = 0.0;
            double cLL_local = 0.0, cLR_local = 0.0, cRR_local = 0.0;
            double lufsSumL_local = lufsSumL, lufsSumR_local = lufsSumR;
            int    lufsSamples_local = lufsBlockSamples;
            // Hoist the coefficient table out of the per-sample loop — one
            // array-of-arrays deref per block instead of 4 per sample.
            var    tpCoeffs = TruePeakCoeffs.Table;

            for (int s = 0; s < n; s++)
            {
                if      (mMute < tgtMasterMute) mMute = MathF.Min(mMute + rampStep, tgtMasterMute);
                else if (mMute > tgtMasterMute) mMute = MathF.Max(mMute - rampStep, tgtMasterMute);

                mGainCur += (tgtMasterGain - mGainCur) * glideK;
                if (MathF.Abs(tgtMasterGain - mGainCur) < 1e-4f) mGainCur = tgtMasterGain;

                float g  = mGainCur * mMute;
                float mL = master[s].L * g;
                float mR = master[s].R * g;
                master[s].L = mL;
                master[s].R = mR;

                // ── Master meter taps (all on the final master samples) ────

                // Master RMS sums — cheap (2 MACs), always on.
                sqL_local += mL * (double)mL;
                sqR_local += mR * (double)mR;

                if (wantDetail)
                {
                    // Correlation sums use full-scale-normalised samples so
                    // the ratio is dimensionless and Pearson's r reads clean.
                    double nL = mL * (1.0 / PedalGainMultiMachine.FullScale);
                    double nR = mR * (1.0 / PedalGainMultiMachine.FullScale);
                    cLL_local += nL * nL;
                    cLR_local += nL * nR;
                    cRR_local += nR * nR;

                    // True peak: 4× oversampling via a 12-tap windowed-sinc
                    // reconstruction filter. Two cost optimisations vs the
                    // naive form, both measured:
                    //
                    //  • A 12-element SHIFT REGISTER, not a ring buffer with
                    //    modulo indexing. The naive `(idx - j + 12) % 12`
                    //    costs 96 integer divisions per sample — measured at
                    //    ~40% of total Work cost. The shift is 11 register
                    //    moves, far cheaper, and lets the tap expression be
                    //    fully unrolled with no bounds checks.
                    //
                    //  • Phase 0 is SKIPPED. Its coefficients are all zero
                    //    except tap 6 = 1, so its output is exactly a past
                    //    input sample — already covered by the sample-peak
                    //    term below. Computing it costs 24 MACs to
                    //    rediscover a value we already have.
                    //
                    // Sample peak is folded in directly (that IS phase 0), so
                    // true peak ≥ sample peak still holds by construction.
                    tp11L = tp10L; tp10L = tp9L; tp9L = tp8L; tp8L = tp7L;
                    tp7L  = tp6L;  tp6L  = tp5L; tp5L = tp4L; tp4L = tp3L;
                    tp3L  = tp2L;  tp2L  = tp1L; tp1L = tp0L; tp0L = mL;

                    tp11R = tp10R; tp10R = tp9R; tp9R = tp8R; tp8R = tp7R;
                    tp7R  = tp6R;  tp6R  = tp5R; tp5R = tp4R; tp4R = tp3R;
                    tp3R  = tp2R;  tp2R  = tp1R; tp1R = tp0R; tp0R = mR;

                    // Phase 0 == a delayed input sample; take it as sample peak.
                    float absL0 = MathF.Abs(mL), absR0 = MathF.Abs(mR);
                    if (absL0 > tpL_local) tpL_local = absL0;
                    if (absR0 > tpR_local) tpR_local = absR0;

                    // Phases 1-3: the intermediate 4×-oversampled points.
                    for (int p = 1; p < 4; p++)
                    {
                        var c = tpCoeffs[p];
                        float yL = c[0]  * tp0L  + c[1]  * tp1L  + c[2]  * tp2L
                                 + c[3]  * tp3L  + c[4]  * tp4L  + c[5]  * tp5L
                                 + c[6]  * tp6L  + c[7]  * tp7L  + c[8]  * tp8L
                                 + c[9]  * tp9L  + c[10] * tp10L + c[11] * tp11L;
                        float yR = c[0]  * tp0R  + c[1]  * tp1R  + c[2]  * tp2R
                                 + c[3]  * tp3R  + c[4]  * tp4R  + c[5]  * tp5R
                                 + c[6]  * tp6R  + c[7]  * tp7R  + c[8]  * tp8R
                                 + c[9]  * tp9R  + c[10] * tp10R + c[11] * tp11R;
                        float aL = MathF.Abs(yL), aR = MathF.Abs(yR);
                        if (aL > tpL_local) tpL_local = aL;
                        if (aR > tpR_local) tpR_local = aR;
                    }

                    // K-weighting for LUFS blocks. Two biquads in series per
                    // side, double precision. Samples pre-scaled to full-scale
                    // so kL is dimensionless in the ITU-R BS.1770 sense.
                    double kL = hpL.Process(shelfL.Process(mL * (1.0 / PedalGainMultiMachine.FullScale)));
                    double kR = hpR.Process(shelfR.Process(mR * (1.0 / PedalGainMultiMachine.FullScale)));
                    lufsSumL_local += kL * kL;
                    lufsSumR_local += kR * kR;
                    lufsSamples_local++;

                    // Complete a 100 ms block?
                    if (lufsSamples_local >= lufsBlockLen)
                    {
                        double e = (lufsSumL_local + lufsSumR_local) / lufsBlockLen;
                        // LUFS = -0.691 + 10·log10(e); guard e ≤ 0 → -inf.
                        float lufs = e > 0.0
                            ? (float)(-0.691 + 10.0 * Math.Log10(e))
                            : float.NegativeInfinity;
                        if (pendingLufsCount < pendingLufs.Length)
                            pendingLufs[pendingLufsCount++] = lufs;
                        lufsSumL_local    = 0.0;
                        lufsSumR_local    = 0.0;
                        lufsSamples_local = 0;
                    }
                }
                else
                {
                    // Detail analysis is off — still track plain sample peak
                    // so the param panel's meters keep working. This is one
                    // compare per side, versus 72 MACs + 4 biquads above.
                    float absL0 = MathF.Abs(mL), absR0 = MathF.Abs(mR);
                    if (absL0 > tpL_local) tpL_local = absL0;
                    if (absR0 > tpR_local) tpR_local = absR0;
                }
            }
            masterMuteCur    = mMute;
            masterGlideCur   = mGainCur;
            lufsSumL         = lufsSumL_local;
            lufsSumR         = lufsSumR_local;
            lufsBlockSamples = lufsSamples_local;

            blockTpL = tpL_local;
            blockTpR = tpR_local;
            blockSqL = sqL_local;
            blockSqR = sqR_local;
            blockCLL = cLL_local;
            blockCLR = cLR_local;
            blockCRR = cRR_local;

            // Fold this Work's stats into every reader slot. Skipped entirely
            // when nothing is polling — avoids taking the lock on the audio
            // thread for data no one will read.
            if (wantAny) MergeBlockIntoSlots(n);

            // Return true if any input contributed. Ramps mid-fade after a
            // disconnect are handled by parking-at-target (above), so a fully
            // silent block IS silent — no lingering tail. Step 3 will refine
            // with a meter-aware return once loudness accumulators exist.
            return anyInput;
        }
    }
}
