// Pedal Gain Multi – ReBuzz managed effect machine
//
// A multi-input stereo gain / summing mixer for ReBuzz.
//   • Six stereo inputs, one stereo output
//   • Single Gain control (0–200 %, default 100 = unity)
//   • Per-input peak meters + stereo output peak meter, all with instant
//     attack / exponential release computed on the audio thread so the GUI
//     just reads the latest values.
//
// Build:
//   dotnet build PedalGainMulti.csproj -c Release /p:ReBuzzDir="C:\Program Files\ReBuzz"

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMulti
{
    [MachineDecl(
        Name        = "Pedal Gain Multi",
        ShortName   = "PGainMul",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = PedalGainMultiMachine.NumInputs,
        OutputCount = PedalGainMultiMachine.NumOutputs)]
    public class PedalGainMultiMachine : IBuzzMachine
    {
        // ── I/O configuration ────────────────────────────────────────────────
        // Six stereo inputs summed to one stereo output.
        public const int NumInputs  = 6;
        public const int NumOutputs = 1;

        // ReBuzz Sample buffers use the Buzz convention: full scale is ±32768,
        // NOT ±1.0. Every value stored in a meter field is divided by this so
        // the GUI can treat 1.0 as 0 dBFS. Omitting this normalization is the
        // classic "meter pinned to red" bug.
        const float FULL_SCALE = 32768f;

        readonly IBuzzMachineHost host;
        readonly string[] inputLabels = new string[NumInputs];

        // ── Meter state (audio-thread writes, UI-thread reads) ───────────────
        // One peak value per input (mono — max of |L|, |R|), plus L/R for the
        // output after gain. All values are normalized so 1.0 == 0 dBFS.
        //
        // volatile applies to references, not array elements — but 32-bit
        // float writes are atomic on x64 and the GUI only needs eventual
        // consistency, which is fine for a VU display. This matches the
        // pattern used by Pedal Patcher's VuLevel[,] field.
        public readonly float[] MeterIn = new float[NumInputs];
        public volatile float MeterL;
        public volatile float MeterR;

        // ── Solo state ───────────────────────────────────────────────────────
        // Audio thread reads this array from Work(); UI thread reads it to
        // drive the solo-button highlight. Writes come from ReBuzz's own
        // parameter-setter path (on the audio thread) — triggered either by
        // a GUI button click calling IParameter.SetValue, or by the pattern
        // editor, or by loading a song. A single bool is byte-atomic so no
        // synchronization is needed for a 6-element flag array.
        public readonly bool[] Solo = new bool[NumInputs];

        // ── Per-input mute state ─────────────────────────────────────────────
        // Mirrors the InMute{N} parameters in the same way Solo[] mirrors the
        // Solo{N} parameters. Each input has its own ramped gain; all share
        // the global Inertia setting via the same muteStep computation in Work.
        public readonly bool[]  InMute            = new bool[NumInputs];
        readonly        float[] currentInMuteGain = new float[NumInputs];
        bool                    inMuteInitialized;

        // ── Mute state with inertia ──────────────────────────────────────────
        // The Mute parameter just flips a target; currentMuteGain ramps toward
        // it inside Work() to avoid clicks. Linear ~25 ms fade.
        // muteInitialized snaps the ramp to its target on the first Work()
        // after construction, so loading a song with Mute=true doesn't cause
        // an audible fade-down at song start.
        float currentMuteGain = 1f;
        bool  muteInitialized;

        int cachedSr;

        // ── Gain parameter ───────────────────────────────────────────────────
        // 0   → mute
        // 100 → unity (0 dB)
        // 200 → +6 dB
        [ParameterDecl(
            Name        = "Gain",
            Description = "Output gain in percent. 100 = unity (0 dB), 200 = +6 dB.",
            MinValue    = 0,
            MaxValue    = 200,
            DefValue    = 100)]
        public int Gain { get; set; } = 100;

        // ── Solo switches (one per input) ────────────────────────────────────
        // bool → ParameterType.Switch. When ANY input is soloed, only soloed
        // inputs route to the output. Each property just mirrors one slot of
        // the Solo[] array so Work() only has to touch one cache line.
        [ParameterDecl(Name = "Solo 1", Description = "Solo input 1", DefValue = 0)]
        public bool Solo1 { get => Solo[0]; set => Solo[0] = value; }

        [ParameterDecl(Name = "Solo 2", Description = "Solo input 2", DefValue = 0)]
        public bool Solo2 { get => Solo[1]; set => Solo[1] = value; }

        [ParameterDecl(Name = "Solo 3", Description = "Solo input 3", DefValue = 0)]
        public bool Solo3 { get => Solo[2]; set => Solo[2] = value; }

        [ParameterDecl(Name = "Solo 4", Description = "Solo input 4", DefValue = 0)]
        public bool Solo4 { get => Solo[3]; set => Solo[3] = value; }

        [ParameterDecl(Name = "Solo 5", Description = "Solo input 5", DefValue = 0)]
        public bool Solo5 { get => Solo[4]; set => Solo[4] = value; }

        [ParameterDecl(Name = "Solo 6", Description = "Solo input 6", DefValue = 0)]
        public bool Solo6 { get => Solo[5]; set => Solo[5] = value; }

        // ── Mute (with inertia) ──────────────────────────────────────────────
        // IMPORTANT — declared LAST in the original release so parameter
        // ordering for existing songs (Gain, Solo 1..6) was unchanged. Old
        // songs that don't have a Mute value stored fall back to DefValue=0
        // (= not muted), so the upgrade was fully backwards-compatible.
        [ParameterDecl(
            Name        = "Mute",
            Description = "Mute the output (fade time set by Inertia)",
            DefValue    = 0)]
        public bool Mute { get; set; }

        // ── Inertia ──────────────────────────────────────────────────────────
        // Fade time (ms) used when Mute toggles. Declared AFTER Mute so older
        // songs (which had Mute hard-coded to a 25 ms ramp) load with the
        // default value of 25 and sound identical to before. Range 0..500 ms;
        // 0 means an instant snap (no declick).
        [ParameterDecl(
            Name            = "Inertia",
            Description     = "Mute fade time in milliseconds (0 = instant)",
            MinValue        = 0,
            MaxValue        = 500,
            DefValue        = 25,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Inertia { get; set; } = 25;

        // ── Per-input mute switches ──────────────────────────────────────────
        // Declared LAST so older songs (which had no per-input mutes) load
        // with all of these at DefValue=0 (= not muted). Each shares the
        // global Inertia value; the ramp lives inside Work() alongside the
        // output mute's ramp.
        //
        // Note: the params window will show "Mute" (output) followed later
        // by "Mute 1".."Mute 6". The descriptions disambiguate; in the GUI
        // the per-input buttons sit in the IN section and the output button
        // in the OUT section, so there's no visual ambiguity in normal use.
        [ParameterDecl(Name = "Mute 1", Description = "Mute input 1", DefValue = 0)]
        public bool InMute1 { get => InMute[0]; set => InMute[0] = value; }

        [ParameterDecl(Name = "Mute 2", Description = "Mute input 2", DefValue = 0)]
        public bool InMute2 { get => InMute[1]; set => InMute[1] = value; }

        [ParameterDecl(Name = "Mute 3", Description = "Mute input 3", DefValue = 0)]
        public bool InMute3 { get => InMute[2]; set => InMute[2] = value; }

        [ParameterDecl(Name = "Mute 4", Description = "Mute input 4", DefValue = 0)]
        public bool InMute4 { get => InMute[3]; set => InMute[3] = value; }

        [ParameterDecl(Name = "Mute 5", Description = "Mute input 5", DefValue = 0)]
        public bool InMute5 { get => InMute[4]; set => InMute[4] = value; }

        [ParameterDecl(Name = "Mute 6", Description = "Mute input 6", DefValue = 0)]
        public bool InMute6 { get => InMute[5]; set => InMute[5] = value; }

        // ── Construction ─────────────────────────────────────────────────────
        public PedalGainMultiMachine(IBuzzMachineHost host)
        {
            this.host = host;

            for (int i = 0; i < NumInputs; i++)
                inputLabels[i] = $"In {i + 1}";

            // host.Machine is null inside the constructor, so we can't set
            // InputChannelCount / OutputChannelCount here. Wait for the
            // song to notify us that this machine has joined the graph.
            Global.Buzz.Song.MachineAdded   += OnMachineAdded;
            Global.Buzz.Song.MachineRemoved += OnMachineRemoved;
        }

        void OnMachineAdded(IMachine m)
        {
            if (m != host.Machine) return;
            host.InputChannelCount  = NumInputs;
            host.OutputChannelCount = NumOutputs;
        }

        void OnMachineRemoved(IMachine m)
        {
            if (m != host.Machine) return;
            Global.Buzz.Song.MachineAdded   -= OnMachineAdded;
            Global.Buzz.Song.MachineRemoved -= OnMachineRemoved;
        }

        // ── Channel naming (shown in the connection-circle tooltip) ──────────
        public string GetChannelName(bool input, int index)
        {
            if (input  && index >= 0 && index < NumInputs) return inputLabels[index];
            if (!input && index == 0)                      return "Out";
            return "";
        }

        // ── Work — EffectBlockMulti signature ────────────────────────────────
        // ReBuzz hands us one Sample[] per connection:
        //   input[i]  — buffer for input channel i  (null if nothing connected)
        //   output[o] — buffer for output channel o (may be null when muted)
        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (mode == WorkModes.WM_NOIO) return false;

            Sample[] outBuf = (output != null && output.Count > 0) ? output[0] : null;
            if (outBuf == null) return false;

            // Shared meter decay — depends only on buffer length and sample rate.
            // ~-20 dB per second (same ballistics as PedalLimit).
            int sr = host.MasterInfo.SamplesPerSec;
            if (sr > 0) cachedSr = sr;
            float decay = cachedSr > 0
                ? MathF.Exp(-2.302585f * n / cachedSr)
                : 0.95f;

            float g = Gain * 0.01f;   // 0..200 → 0..2.0 linear

            // Per-sample step for a linear fade of length `Inertia` ms. Used
            // by BOTH the per-input mute ramps and the output mute ramp —
            // single source of truth for fade time.
            // Inertia=0 (or no sample rate yet) → snap to target in one sample.
            float fadeSeconds = Inertia * 0.001f;
            float muteStep = (fadeSeconds > 0f && cachedSr > 0)
                ? 1f / (cachedSr * fadeSeconds)
                : 1f;

            // First-Work snap for per-input mute gains, mirroring what we do
            // for the output mute below. Without this, loading a song where
            // some inputs are pre-muted would fade-down on song start.
            if (!inMuteInitialized)
            {
                for (int i = 0; i < NumInputs; i++)
                    currentInMuteGain[i] = InMute[i] ? 0f : 1f;
                inMuteInitialized = true;
            }

            // 1. Zero the output buffer.
            for (int s = 0; s < n; s++)
            {
                outBuf[s].L = 0f;
                outBuf[s].R = 0f;
            }

            // Is any input soloed? If so, only soloed inputs route to output.
            // Per-input mute still applies — mute beats solo (DAW convention).
            bool anySolo = false;
            for (int i = 0; i < NumInputs; i++)
                if (Solo[i]) { anySolo = true; break; }

            // 2. Walk every input slot — ramp its per-input mute, sum its
            //    contribution into the output (gated by solo), and update its
            //    meter from the RAW signal (pre-mute, pre-solo) so the meter
            //    answers "is there signal arriving?" rather than "what's
            //    reaching the mix?". Disconnected slots fade the meter to
            //    zero and snap the per-input mute gain to its target so a
            //    later reconnect doesn't glitch.
            bool anyInput = false;
            int inCount = input?.Count ?? 0;
            for (int i = 0; i < NumInputs; i++)
            {
                Sample[] inBuf = (i < inCount) ? input[i] : null;
                if (inBuf == null)
                {
                    MeterIn[i] = MeterIn[i] * decay;
                    currentInMuteGain[i] = InMute[i] ? 0f : 1f;
                    continue;
                }
                anyInput = true;

                float p = 0f;
                float effSoloGain = (anySolo && !Solo[i]) ? 0f : 1f;
                float inTarget = InMute[i] ? 0f : 1f;
                float inGain = currentInMuteGain[i];

                // One unified loop: ramp per sample, multiply by inGain (and
                // by the solo gate), accumulate into output, collect raw peak
                // for the meter. When solo-muted, effSoloGain is 0 and the
                // adds are no-ops, but the ramp still progresses so flipping
                // mute or solo never produces a stale-gain glitch.
                for (int s = 0; s < n; s++)
                {
                    float l = inBuf[s].L;
                    float r = inBuf[s].R;

                    if (inGain < inTarget)
                        inGain = MathF.Min(inGain + muteStep, inTarget);
                    else if (inGain > inTarget)
                        inGain = MathF.Max(inGain - muteStep, inTarget);

                    float effG = inGain * effSoloGain;
                    outBuf[s].L += l * effG;
                    outBuf[s].R += r * effG;

                    float a = MathF.Max(l < 0f ? -l : l, r < 0f ? -r : r);
                    if (a > p) p = a;
                }

                currentInMuteGain[i] = inGain;
                MeterIn[i] = MathF.Max(p / FULL_SCALE, MeterIn[i] * decay);
            }

            // 3. Apply Gain × output-mute-ramp and collect per-channel output
            //    peaks. Same single-multiply-per-sample shape as the per-input
            //    stage above. On the first Work() call we snap the output mute
            //    gain to its target so song-loaded mutes don't fade-down.
            float targetMuteGain = Mute ? 0f : 1f;
            if (!muteInitialized)
            {
                currentMuteGain = targetMuteGain;
                muteInitialized = true;
            }

            float peakL = 0f, peakR = 0f;
            for (int s = 0; s < n; s++)
            {
                if (currentMuteGain < targetMuteGain)
                    currentMuteGain = MathF.Min(currentMuteGain + muteStep, targetMuteGain);
                else if (currentMuteGain > targetMuteGain)
                    currentMuteGain = MathF.Max(currentMuteGain - muteStep, targetMuteGain);

                float effG = g * currentMuteGain;
                float l = outBuf[s].L * effG;
                float r = outBuf[s].R * effG;
                outBuf[s].L = l;
                outBuf[s].R = r;

                float al = l < 0f ? -l : l;
                float ar = r < 0f ? -r : r;
                if (al > peakL) peakL = al;
                if (ar > peakR) peakR = ar;
            }

            // 4. Update output meters (also normalized).
            MeterL = MathF.Max(peakL / FULL_SCALE, MeterL * decay);
            MeterR = MathF.Max(peakR / FULL_SCALE, MeterR * decay);

            return anyInput || MeterL > 1e-6f || MeterR > 1e-6f;
        }
    }
}
