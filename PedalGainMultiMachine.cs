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

            // 1. Zero the output buffer.
            for (int s = 0; s < n; s++)
            {
                outBuf[s].L = 0f;
                outBuf[s].R = 0f;
            }

            // Is any input soloed? If so, only soloed inputs route to output.
            // If nothing is soloed, behaviour is unchanged from before.
            bool anySolo = false;
            for (int i = 0; i < NumInputs; i++)
                if (Solo[i]) { anySolo = true; break; }

            // 2. Walk every input slot — sum audible ones into the output,
            //    compute per-input peak for the meter (always — solo doesn't
            //    hide the input level, only its contribution to the mix),
            //    and let disconnected slots fade their meter to zero.
            bool anyInput = false;
            int inCount = input?.Count ?? 0;
            for (int i = 0; i < NumInputs; i++)
            {
                Sample[] inBuf = (i < inCount) ? input[i] : null;
                if (inBuf == null)
                {
                    MeterIn[i] = MeterIn[i] * decay;
                    continue;
                }
                anyInput = true;

                float p = 0f;
                bool muted = anySolo && !Solo[i];

                if (muted)
                {
                    // Meter only — don't contribute to the mix.
                    for (int s = 0; s < n; s++)
                    {
                        float l = inBuf[s].L, r = inBuf[s].R;
                        float a = MathF.Max(l < 0f ? -l : l, r < 0f ? -r : r);
                        if (a > p) p = a;
                    }
                }
                else
                {
                    // Audible — sum into output AND feed the meter.
                    for (int s = 0; s < n; s++)
                    {
                        float l = inBuf[s].L, r = inBuf[s].R;
                        outBuf[s].L += l;
                        outBuf[s].R += r;
                        float a = MathF.Max(l < 0f ? -l : l, r < 0f ? -r : r);
                        if (a > p) p = a;
                    }
                }
                // Instant attack, exponential release — normalized to 0 dBFS = 1.
                MeterIn[i] = MathF.Max(p / FULL_SCALE, MeterIn[i] * decay);
            }

            // 3. Apply gain and collect per-channel output peaks.
            float peakL = 0f, peakR = 0f;
            for (int s = 0; s < n; s++)
            {
                float l = outBuf[s].L * g;
                float r = outBuf[s].R * g;
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
