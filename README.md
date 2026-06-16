# Pedal Gain Multi

A multi-input gain / summing mixer for [ReBuzz](https://github.com/wasteddesign/ReBuzz),
derived from **Pedal Gain** with ReBuzz Multi-In support.

## Features

- **6 stereo inputs → 1 stereo output** — connect up to six signals; they're summed at unity before the gain stage.
- **Single Gain control** — 0 … 200 % (100 = unity / 0 dB, 200 = +6 dB).
- **Per-input solo** — one-click "S" button beside each input meter. If any
  input is soloed, only soloed inputs reach the mix; if none are, everything
  routes normally. Solo state is a real parameter — saved with the song,
  automatable from the pattern editor, and undoable.
- **Output mute with adjustable inertia** — single "M" button at the top of
  the output section. The fade time is set by the **Inertia** parameter
  (0–500 ms, default 25 ms), so you can dial it from a tight 5 ms declick all
  the way out to a half-second fade. `Inertia = 0` snaps instantly. Both
  Mute and Inertia are real parameters — saved with the song, automatable,
  undoable. Backwards compatible: songs saved before either parameter
  existed load with Mute = false and Inertia = 25, so behaviour is
  identical to previous versions.
- **Metering at the top of the parameters window** — six pre-solo input
  peak meters plus a stereo output meter. Instant-attack / exponential-release
  ballistics on the audio thread, lightweight 33 ms UI redraw, held-peak line
  with synchronized dB readout.

## Requirements

- [ReBuzz](https://github.com/wasteddesign/ReBuzz) (1812-preview or later)
- [.NET 10 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Building

```powershell
dotnet build PedalGainMulti.csproj -c Release /p:ReBuzzDir="C:\Program Files\ReBuzz"
```

The output `Pedal Gain Multi.NET.dll` is written directly to `<ReBuzzDir>\Gear\Effects\`.
Restart ReBuzz and the machine appears as **Pedal Gain Multi** under Effects.

## How the Multi-In plumbing works

Three pieces, copied from the Pedal Patch convention:

1. **`MachineDecl`** declares `InputCount = 6, OutputCount = 1`.
2. **`Song.MachineAdded`** event is hooked in the constructor so that
   `host.InputChannelCount` / `host.OutputChannelCount` can be written as
   soon as the machine joins the graph — `host.Machine` is null while the
   constructor is running, so it can't be done there.
3. **`Work(IList<Sample[]>, IList<Sample[]>, int, WorkModes)`** — the
   EffectBlockMulti signature. `input[i]` is the stereo buffer for channel
   `i` (or `null` if nothing is connected to that slot).

`GetChannelName(bool input, int index)` provides the label shown in the
connection-circle tooltip in ReBuzz's machine view.

## How the meter stays cheap

Meter ballistics run **in the audio thread**, inside `Work()`:

```csharp
MeterL = MathF.Max(peakL, MeterL * decay);
MeterR = MathF.Max(peakR, MeterR * decay);
```

`decay = exp(-2.3 · n / sr)` gives a steady -20 dB/second release.
`MeterL` and `MeterR` are `volatile float` — the UI thread reads them
directly without any lock, and the 33 ms `DispatcherTimer` just repaints
the two bar widths plus the peak-hold line. No allocations per frame, no
cross-thread queues.

## License

MIT
