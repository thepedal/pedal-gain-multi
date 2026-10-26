# Pedal Gain Multi

A multi-track mixer for [ReBuzz](https://github.com/wasteddesign/ReBuzz) with
per-channel faders, pan, mute/solo, direct outs and broadcast-standard
metering.

## Features

**Mixing**

- **1–24 stereo channels**, set by the machine's track count. New machines
  start with 6. Each channel is an input pin; the strip is
  Mono → Mute → Volume → Pan.
- **Master output plus a direct out per channel.** Output 0 is the master
  mix; output *k+1* carries channel *k* post-mute, post-fader and post-pan
  but **pre-solo**, so Solo, the master Gain and Master Mute never touch a
  direct out.
- **dB faders** on every channel and the master, 0.5 dB per step from −80 dB
  to +10 dB, with a console taper that gives the −20…+10 dB region most of
  the throw.
- **Pan** with the right law for the source: balance on stereo channels,
  constant-power on channels summed to mono.
- **Click-free everything.** Gain, Volume, Pan and Mono glide over ~10 ms;
  mute and solo fade over an adjustable **Inertia** (0–500 ms, default 25).

**Metering**

- Per-channel peak meters with a switchable **PRE / POST** tap, IEC
  ballistics (20 dB fall in 1.7 s), 3-second peak hold and clip indication.
- **True-peak** master metering — 4× oversampled, so inter-sample peaks that
  a sample-peak meter misses still show up.
- A separate **meter window** per machine with tall bars for every channel
  and the master pair: RMS solid over a translucent peak wash, hold lines,
  clip boxes, M/S badges and dimming for channels not reaching the master.
- **Loudness to EBU R128 / ITU-R BS.1770** — momentary, short-term and gated
  integrated LUFS against a −23 or −14 LUFS target, plus a stereo
  correlation meter.

## Parameters

| Scope | Name | Range | Default |
|---|---|---|---|
| Global | `Gain` | 0–181 (−∞ … +10 dB) | 161 (0 dB) |
| Global | `Master Mute` | switch | off |
| Global | `Inertia` | 0–500 ms | 25 ms |
| Track | `Solo` | switch | off |
| Track | `Mute` | switch | off |
| Track | `Volume` | 0–181 (−∞ … +10 dB) | 161 (0 dB) |
| Track | `Pan` | 0–128 (L … C … R) | 64 (centre) |
| Track | `Mono` | switch | off |

Level codes are 0.5 dB apart: 0 is −∞, 1 is −80 dB, 149 is −6 dB, 161 is
0 dB and 181 is +10 dB. Hovering any parameter shows its value in dB, LR
percentage or milliseconds.

## Using it

Connect sources to the input pins; each one is a channel. The parameter
window gives you a strip per channel — mute, solo, meter with held-peak
readout, VOL and PAN faders and a MONO toggle — over a master section.

- `−` and `+` in the IN header change the channel count.
- `PRE` / `POST` switches the channel meters between the raw input and the
  post-fader signal.
- Clicking any meter clears every held peak and clip light.
- Faders respond to dragging, double-click (reset to default) and the mouse
  wheel (1 dB per notch, 0.5 dB with Ctrl held).
- `METERS` opens the meter bridge. Its `T -23` / `T -14` button picks the
  loudness target and `RESET` restarts integration.

## Changes in 2.0.2

**Fixed: ReBuzz's own parameter sliders could show the wrong value.**

After 2.0.1 the audio and the machine's meter strip were correct, but the
slider list in the parameter window could disagree with both — showing a
track as unmuted while it was silent, or at 0 dB while the meter read -inf.

This is the second half of the same ReBuzz bug. `MachineWorkInstance`
raises the UI change event from the very same dictionary:

```csharp
foreach (var paramTrack in Machine.parametersChanged)
    par.InvokeEvents(buzz, track);          // once per parameter, last track only
```

and `ParameterVM` reads its value lazily (`parameter.GetValue(track)`),
repainting only when that event arrives. So the stored value was always
right; the slider simply was never told to look again. This machine's own
panel never showed it because that panel re-reads `GetValue` every frame.

2.0.2 detects the missed events exactly rather than by guesswork. The host
calls our setter and raises the UI event from the same dictionary entry, so
"our setter ran for (parameter, track)" is equivalent to "the UI was told
about (parameter, track)". Every setter call records what the UI has
therefore seen; any track whose value has since moved away from that shadow
had its event dropped, and the GUI timer nudges it — writing back the value
the parameter already holds, so the nudge cannot alter anything, and
carrying ReBuzz's do-not-record flag (`track | (1 << 16)`) so it never
leaks into pattern recording.

At most one nudge per parameter per frame is issued, because two in the
same tick would collide in the dictionary slot being worked around. A
24-fader bank therefore settles in well under a second, and a single fader
move issues no nudges at all.

## Changes in 2.0.1

**Fixed: MIDI-driven mute and volume could silently fail to take effect.**

With MIDI controls bound to several tracks, a track could stay muted after
being unmuted, or stay silent until its volume fader was nudged a second
time.

The cause is in ReBuzz rather than this machine, but the machine has to work
around it. `ParameterCore.SetValue` records a parameter change as:

```csharp
pvalues[track] = value;
machine.parametersChanged[this] = track;   // keyed by parameter, not by track
```

`ManagedMachineHost.Tick` then walks that dictionary and calls the setter
**once per parameter**, for whichever track wrote last. When two tracks write
the same parameter inside one tick — which a bank of MIDI faders or a row of
mute buttons does constantly — every write but the last is discarded with no
error. A 24-fader bank moved together lost 23 of its 24 moves.

Every track's value is still present in the parameter's `pvalues` array at
setter time (ReBuzz only clears it at the end of the tick), so 2.0.1 reads
them back and re-applies the dropped writes. This is the same recovery used
elsewhere in the Pedal machine family, and it tolerates both `pvalues`
representations ReBuzz has shipped (`ConcurrentDictionary<int,int>` and
`int[]`), degrading to the old behaviour rather than crashing if the field
changes shape again.

**Also fixed: out-of-range parameter values could crash the audio thread.**
`Gain` and `Volume` index a 182-entry gain table, so a value outside 0–181
raised an `IndexOutOfRangeException` inside `Work`. ReBuzz's own dispatch
range-checks before calling a setter, so this was not reachable from MIDI or
patterns — but a control machine writing `IParameter.SetValue` directly
bypasses that check. All parameter values are now clamped on the way in.

## Upgrading from 1.x

**2.0.0 is a breaking change.** The parameter layout is completely
different — 1.x's `Solo 1..6`, `Mute 1..6`, single `Mute` and percentage
`Gain` are gone, replaced by per-track parameters and dB faders. A song
saved with 1.x will load with the machine present, but its Pedal Gain Multi
settings will not carry over. Check any song that used it before relying on
the result.

## Requirements

- ReBuzz 1813-preview or later (needs `EffectBlockMulti`)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (Windows x64)

## Building

```powershell
dotnet build PedalGainMulti.csproj -c Release
```

The DLL is written straight to `C:\Program Files\ReBuzz\Gear\Effects\`.
If ReBuzz lives elsewhere:

```powershell
dotnet build PedalGainMulti.csproj -c Release /p:ReBuzzDir="D:\ReBuzz"
```

Close ReBuzz before building — it holds the DLL open while running.

## Notes on the implementation

A few decisions worth knowing if you read or modify the source.

**Metering is gated on a live reader.** True-peak filtering and K-weighting
are expensive, and nothing needs them when no meter is on screen. Each
`TakeMeters` call stamps a block counter; the audio thread skips the
expensive analysis about two seconds after the last poll and resumes from
the next block when polling restarts. A machine sitting in a song with its
windows closed costs very little.

**Four reader slots.** The parameter panel reads slot 0 and meter windows
read slots 1–3, each with its own accumulator, so two readers never steal
each other's peaks and no peak is lost between polls.

**Loudness averages in the energy domain, never in dB.** Averaging LUFS
values directly is the classic way to get integrated loudness subtly wrong;
EBU 3341 cases 3–5 exist to catch exactly that. Verified against all five
cases at 44.1 and 48 kHz, each landing within 0.07 LU of target.

**Track-parameter writes are recovered from `pvalues`.** ReBuzz delivers one
setter call per parameter per tick, for the last track that wrote it, so
simultaneous writes across tracks are dropped. Each track setter re-reads the
parameter's `pvalues` array and re-applies anything present that tick. The
poll arms itself only after observing a `NoValue`, which proves ReBuzz has
cleared the array at least once — before that the array is all zeros, and
zero is a valid Volume (-inf) and Pan (hard left), so an unguarded poll would
drive every untouched track to silence.

**Meter gradients use `BrushMappingMode.Absolute`** over the full bar
length. Mapping relative to the lit portion instead makes every bar run
green-to-red regardless of level, so quiet signals show red at the tip.

## License

MIT
