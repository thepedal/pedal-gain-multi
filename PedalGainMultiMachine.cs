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

            // 2. Walk every input slot — sum connected ones into the output,
            //    compute per-input peak, update each input meter. Disconnected
            //    slots just let their meter fade to zero.
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
                for (int s = 0; s < n; s++)
                {
                    float l = inBuf[s].L;
                    float r = inBuf[s].R;
                    outBuf[s].L += l;
                    outBuf[s].R += r;
                    float a = MathF.Max(l < 0f ? -l : l, r < 0f ? -r : r);
                    if (a > p) p = a;
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
