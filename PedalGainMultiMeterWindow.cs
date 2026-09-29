// Pedal Gain Multi — meter maths and meter window (v2.0.0)
//
// Two pure-computation classes, no WPF beyond the zone palette:
//
//   LoudnessMeter — consumes the 100 ms K-weighted LUFS blocks the machine
//                   produces (PedalGainMultiMachine.TakeMeters) and derives
//                   momentary / short-term / gated-integrated loudness per
//                   ITU-R BS.1770 and EBU R128. Also carries the τ = 300 ms
//                   RMS and correlation integrators.
//
//   MeterScale    — the shared dB → bar-height mapping and zone colours used
//                   by BOTH the parameter panel and the meter window, so the
//                   two always agree.
//
// And the window they feed:
//
//   PedalGainMultiMeterWindow — one per machine, opened by the panel's
//                   METERS button. Redraws on CompositionTarget.Rendering
//                   capped near 60 fps.
//   MeterBridge   — every column drawn in one OnRender pass: RMS solid over
//                   a translucent peak wash, ticks over the bars, hold line,
//                   clip box, M/S badges, dimming for inaudible channels.
//   LoudnessView  — the bottom strip: M and S bars on an LU scale around the
//                   target, the integrated figure, and stereo correlation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMulti
{
    // ── Loudness / correlation maths ─────────────────────────────────────────
    //
    // Fed one call per GUI frame with whatever LUFS blocks arrived since the
    // last frame plus the master mean-square and correlation sums. Everything
    // else is derived.
    //
    // Units throughout: LUFS for absolute loudness, LU for relative offsets.
    // "Energy domain" means linear mean-square; "LUFS domain" means the
    // -0.691 + 10·log10(e) rendering of it. Averaging MUST happen in the
    // energy domain — averaging dB values is the classic way to get the
    // integrated figure subtly wrong, and EBU 3341 cases 3-5 are designed
    // to catch exactly that.
    public sealed class LoudnessMeter
    {
        // Ring of the last 30 blocks (3 s at 100 ms/block) — enough for
        // short-term. Momentary reads the last 4 of the same ring.
        const int RingSize = 30;
        readonly float[] ring = new float[RingSize];
        int ringCount;   // total blocks ever seen (saturating at RingSize for indexing)
        int ringHead;    // next write position

        // Gated list for integrated loudness. Each entry is the ENERGY of a
        // 400 ms window (mean of 4 consecutive 100 ms blocks) that passed the
        // -70 LUFS absolute gate. Stored as energy, not LUFS, so the relative
        // gate and the final mean can both be computed without re-exponentiating.
        //
        // A list is the right shape here: R128 integrated loudness is defined
        // over the whole programme, so entries are never evicted — only RESET
        // clears them. At 400 ms per entry that's 9000 entries per hour, which
        // is nothing.
        float[] gated = new float[1024];
        int     gatedCount;

        // Rolling 4-block window for forming 400 ms gating blocks. We push
        // one gating block per 100 ms block once at least 4 exist (i.e. the
        // windows overlap by 75%, per BS.1770's sliding-window definition).
        readonly float[] lastFour = new float[4];
        int              lastFourCount;

        // τ = 300 ms exponential integrators for the RMS and correlation
        // displays. Coefficient recomputed if the frame interval changes.
        double msL, msR;
        double corLL, corLR, corRR;
        bool   integratorsPrimed;

        // Absolute gate from BS.1770: blocks quieter than this never count.
        const double AbsoluteGateLufs = -70.0;
        // Relative gate: 10 LU below the ungated mean.
        const double RelativeGateLu = 10.0;

        // ── Derived values (read by the GUI after each Update) ───────────────

        /// Momentary loudness (400 ms window). NegativeInfinity until 4 blocks exist.
        public float MomentaryLufs { get; private set; } = float.NegativeInfinity;

        /// Short-term loudness (3 s window). NegativeInfinity until 30 blocks exist.
        public float ShortTermLufs { get; private set; } = float.NegativeInfinity;

        /// Gated integrated loudness over the whole programme since the last Reset.
        public float IntegratedLufs { get; private set; } = float.NegativeInfinity;

        /// Smoothed master RMS per side, as linear mean-square (0..1 full scale).
        public float RmsL { get; private set; }
        public float RmsR { get; private set; }

        /// Stereo correlation in -1..+1, or NaN when the signal is too quiet
        /// to be meaningful (the GUI shows "--" for NaN).
        public float Correlation { get; private set; } = float.NaN;

        /// Clears every integrator and the gated list — the RESET button.
        public void Reset()
        {
            ringCount = 0;
            ringHead  = 0;
            Array.Clear(ring, 0, ring.Length);

            gatedCount    = 0;
            lastFourCount = 0;

            msL = msR = 0.0;
            corLL = corLR = corRR = 0.0;
            integratorsPrimed = false;

            MomentaryLufs  = float.NegativeInfinity;
            ShortTermLufs  = float.NegativeInfinity;
            IntegratedLufs = float.NegativeInfinity;
            RmsL = RmsR = 0f;
            Correlation = float.NaN;
        }

        /// Feed one GUI frame's worth of data.
        ///
        ///   blocks       — completed 100 ms LUFS blocks since the last frame
        ///                  (MeterSnapshot.LufsBlocks; may be empty)
        ///   msLinL/msLinR— master mean square per side this frame, 0..1
        ///   cLL/cLR/cRR  — correlation sums this frame, already ÷ count
        ///   dtSeconds    — wall time since the last Update, for the τ = 300 ms
        ///                  integrators. Pass the real frame delta (a Stopwatch
        ///                  delta), not a nominal 1/60 — dropped frames would
        ///                  otherwise make the integrators run slow.
        public void Update(float[] blocks,
                           float msLinL, float msLinR,
                           float cLL, float cLR, float cRR,
                           double dtSeconds)
        {
            if (blocks != null)
                for (int i = 0; i < blocks.Length; i++)
                    PushBlock(blocks[i]);

            RecomputeMomentaryAndShortTerm();
            UpdateIntegrators(msLinL, msLinR, cLL, cLR, cRR, dtSeconds);
        }

        // ── Block handling ──────────────────────────────────────────────────

        void PushBlock(float lufs)
        {
            // -inf blocks (digital silence) are still real blocks for the
            // momentary/short-term windows, but their energy is 0 so they
            // can never pass the absolute gate.
            ring[ringHead] = lufs;
            ringHead = (ringHead + 1) % RingSize;
            if (ringCount < RingSize) ringCount++;

            // Maintain the rolling 4-block (400 ms) window for gating.
            if (lastFourCount < 4)
            {
                lastFour[lastFourCount++] = lufs;
            }
            else
            {
                lastFour[0] = lastFour[1];
                lastFour[1] = lastFour[2];
                lastFour[2] = lastFour[3];
                lastFour[3] = lufs;
            }

            if (lastFourCount < 4) return;

            // A complete 400 ms gating block. Mean in the ENERGY domain.
            double e = 0.0;
            for (int i = 0; i < 4; i++) e += LufsToEnergy(lastFour[i]);
            e *= 0.25;

            double blockLufs = EnergyToLufs(e);
            if (blockLufs <= AbsoluteGateLufs) return;   // absolute gate

            if (gatedCount == gated.Length)
                Array.Resize(ref gated, gated.Length * 2);
            gated[gatedCount++] = (float)e;

            RecomputeIntegrated();
        }

        void RecomputeMomentaryAndShortTerm()
        {
            // Momentary = mean of the last 4 blocks, energy domain.
            if (ringCount >= 4)
            {
                double e = 0.0;
                for (int i = 1; i <= 4; i++)
                    e += LufsToEnergy(ring[((ringHead - i) % RingSize + RingSize) % RingSize]);
                MomentaryLufs = (float)EnergyToLufs(e * 0.25);
            }

            // Short-term = mean of the last 30 blocks, energy domain. Only
            // meaningful once the full 3 s window has filled.
            if (ringCount >= RingSize)
            {
                double e = 0.0;
                for (int i = 0; i < RingSize; i++) e += LufsToEnergy(ring[i]);
                ShortTermLufs = (float)EnergyToLufs(e / RingSize);
            }
        }

        // The two-stage BS.1770 gate. Called whenever a new gating block
        // joins the list.
        void RecomputeIntegrated()
        {
            if (gatedCount == 0) { IntegratedLufs = float.NegativeInfinity; return; }

            // Stage 1: ungated mean of everything that passed the absolute gate.
            double sum = 0.0;
            for (int i = 0; i < gatedCount; i++) sum += gated[i];
            double meanEnergy = sum / gatedCount;

            // Stage 2: relative threshold 10 LU below that mean. Compared in
            // the ENERGY domain — 10 LU is a factor of 10^(10/10) = 10 in energy.
            double relThresholdEnergy = meanEnergy / Math.Pow(10.0, RelativeGateLu / 10.0);

            double sum2 = 0.0;
            int    n2   = 0;
            for (int i = 0; i < gatedCount; i++)
            {
                if (gated[i] > relThresholdEnergy) { sum2 += gated[i]; n2++; }
            }

            IntegratedLufs = n2 > 0
                ? (float)EnergyToLufs(sum2 / n2)
                : float.NegativeInfinity;
        }

        // ── τ = 300 ms integrators ──────────────────────────────────────────

        void UpdateIntegrators(float msLinL, float msLinR,
                               float cLL, float cLR, float cRR,
                               double dtSeconds)
        {
            // First frame: jump straight to the measured values rather than
            // ramping up from zero, so a freshly-opened meter shows the real
            // level immediately instead of a 300 ms swell.
            if (!integratorsPrimed)
            {
                integratorsPrimed = true;
                msL = msLinL; msR = msLinR;
                corLL = cLL;  corLR = cLR;  corRR = cRR;
            }
            else
            {
                // One-pole toward the new value. Using the real frame delta
                // keeps the time constant honest when frames are dropped.
                if (dtSeconds <= 0.0) dtSeconds = 1.0 / 60.0;
                double k = 1.0 - Math.Exp(-dtSeconds / 0.300);

                msL += (msLinL - msL) * k;
                msR += (msLinR - msR) * k;
                corLL += (cLL - corLL) * k;
                corLR += (cLR - corLR) * k;
                corRR += (cRR - corRR) * k;
            }

            RmsL = (float)msL;
            RmsR = (float)msR;

            double denom = Math.Sqrt(corLL * corRR);
            Correlation = denom < 1e-9
                ? float.NaN                                     // too quiet to mean anything
                : (float)Math.Clamp(corLR / denom, -1.0, 1.0);
        }

        // ── LUFS ↔ energy ───────────────────────────────────────────────────
        // LUFS = -0.691 + 10·log10(e)  ⟺  e = 10^((LUFS + 0.691) / 10)
        static double LufsToEnergy(double lufs) =>
            double.IsNegativeInfinity(lufs) ? 0.0 : Math.Pow(10.0, (lufs + 0.691) / 10.0);

        static double EnergyToLufs(double e) =>
            e > 0.0 ? -0.691 + 10.0 * Math.Log10(e) : double.NegativeInfinity;
    }

    // ── Shared meter scale ───────────────────────────────────────────────────
    //
    // One definition of "how tall is a bar at N dB" and "what colour is it",
    // used by the parameter panel and the meter window alike so they can
    // never drift apart.
    //
    // The mapping is piecewise linear between named breakpoints, chosen so
    // the busy -18..0 dB region gets most of the travel. Note the scale runs
    // to +3 dB: the meter window shows headroom above 0 dBFS, while the panel
    // normalises so its bars end exactly at 0 dB (see PanelFrac).
    public static class MeterScale
    {
        static readonly float[] Db   = { -60f, -50f, -40f, -30f, -24f, -18f, -12f, -9f, -6f, -3f, 0f, 3f };
        static readonly float[] Frac = {   0f, 0.06f, 0.14f, 0.25f, 0.33f, 0.43f, 0.56f, 0.64f, 0.73f, 0.84f, 0.95f, 1.00f };

        /// dB → 0..1 position on the full scale (which tops out at +3 dB).
        public static float Fraction(float db)
        {
            if (db <= Db[0])              return Frac[0];
            if (db >= Db[Db.Length - 1])  return Frac[Frac.Length - 1];

            for (int i = 1; i < Db.Length; i++)
            {
                if (db > Db[i]) continue;
                float span = Db[i] - Db[i - 1];
                float t    = span > 0f ? (db - Db[i - 1]) / span : 0f;
                return Frac[i - 1] + t * (Frac[i] - Frac[i - 1]);
            }
            return Frac[Frac.Length - 1];
        }

        /// Fraction of 0 dB on the full scale — the panel's bars stop here.
        public static readonly float ZeroDbFraction = 0.95f;

        /// dB → 0..1 for the PANEL, whose bars end at 0 dB rather than +3.
        public static float PanelFraction(float db) => Fraction(db) / ZeroDbFraction;

        // Zone colours: green below -12 dB, yellow to -3 dB, red above.
        public static readonly Color Green  = Color.FromRgb( 60, 200,  90);
        public static readonly Color Yellow = Color.FromRgb(230, 200,  50);
        public static readonly Color Red    = Color.FromRgb(235,  60,  45);

        /// The zone colour for a given level.
        public static Color ZoneColor(float db) =>
            db < -12f ? Green : (db < -3f ? Yellow : Red);

        /// Build the meter gradient.
        ///
        /// IMPORTANT: the caller must map this with BrushMappingMode.Absolute
        /// over the FULL meter length, not relative to the lit portion of the
        /// bar. Relative mapping was the bug fixed in native v1.6.2 — it makes
        /// every bar run green→red regardless of level, so a quiet signal shows
        /// red at its tip.
        public static LinearGradientBrush CreateGradient(double lengthPx, bool horizontal = true)
        {
            var b = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint  = new System.Windows.Point(0, 0),
                EndPoint    = horizontal
                            ? new System.Windows.Point(lengthPx, 0)
                            : new System.Windows.Point(0, lengthPx),
            };

            float yellowAt = Fraction(-12f) / ZeroDbFraction;
            float redAt    = Fraction(-3f)  / ZeroDbFraction;

            b.GradientStops.Add(new GradientStop(Green,  0.0));
            b.GradientStops.Add(new GradientStop(Green,  yellowAt));
            b.GradientStops.Add(new GradientStop(Yellow, yellowAt));
            b.GradientStops.Add(new GradientStop(Yellow, redAt));
            b.GradientStops.Add(new GradientStop(Red,    redAt));
            b.GradientStops.Add(new GradientStop(Red,    1.0));
            b.Freeze();
            return b;
        }

        /// Linear amplitude (0..1 full scale) → dBFS, floored at -inf guard.
        public static float LinToDb(float lin) =>
            lin > 1e-7f ? 20f * MathF.Log10(lin) : float.NegativeInfinity;

        /// Linear mean-square (0..1) → dBFS.
        public static float MsToDb(float ms) =>
            ms > 1e-14f ? 10f * MathF.Log10(ms) : float.NegativeInfinity;

        /// Make a frozen solid brush. Lives here rather than on one of the
        /// view classes so every view can use it without depending on
        /// another class's static initialiser having run first.
        public static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    // ── Meter window ─────────────────────────────────────────────────────────
    //
    // A big, resizable meter bridge plus a loudness strip. One window per
    // machine: the METERS button on the parameter panel opens it, or brings
    // an existing one forward.
    //
    // Redraws on CompositionTarget.Rendering (capped near 60 fps) rather than
    // a DispatcherTimer, so the meters stay in step with the compositor. That
    // is a STATIC event — failing to unsubscribe on close leaks the window and
    // keeps it rendering forever, so Closed does that unconditionally.
    public sealed class PedalGainMultiMeterWindow : Window
    {
        // One window per machine, so METERS is idempotent.
        static readonly Dictionary<IMachine, PedalGainMultiMeterWindow> Open
            = new Dictionary<IMachine, PedalGainMultiMeterWindow>();

        // Target preference persists across windows within a session.
        static float sharedTargetLufs = -23f;

        readonly IMachine              imachine;
        readonly PedalGainMultiMachine machine;
        readonly LoudnessMeter         loudness = new LoudnessMeter();
        readonly MeterBridge           bridge;
        readonly LoudnessView          loudView;
        readonly ToolbarButton         preBtn, targetBtn;

        readonly Stopwatch clock = new Stopwatch();
        double lastRenderTime;
        double lastFrameTime;
        bool   firstReadDone;
        bool   showPre = true;

        static readonly Brush WindowBg = MeterScale.Frozen(Color.FromRgb(24, 24, 28));
        static readonly Brush StripBg  = MeterScale.Frozen(Color.FromRgb(30, 30, 36));
        static readonly Brush FgDim    = MeterScale.Frozen(Color.FromRgb(120, 120, 135));
        static readonly Brush FgNormal = MeterScale.Frozen(Color.FromRgb(175, 175, 185));
        static readonly FontFamily Mono = new FontFamily("Consolas");

        // (brush helper lives on MeterScale — see MeterScale.Frozen)

        /// Open the window for a machine, or bring the existing one forward.
        public static void ShowFor(IMachine imachine, PedalGainMultiMachine machine)
        {
            if (imachine == null || machine == null) return;

            if (Open.TryGetValue(imachine, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var w = new PedalGainMultiMeterWindow(imachine, machine);
            Open[imachine] = w;
            w.Show();
        }

        PedalGainMultiMeterWindow(IMachine imachine, PedalGainMultiMachine machine)
        {
            this.imachine = imachine;
            this.machine  = machine;

            int channels = Math.Max(1, imachine.TrackCount);

            Title      = (imachine.Name ?? "Pedal Gain Multi") + " — Meters";
            Width      = Math.Max(280, 70 + (channels + 2) * 34);
            Height     = 480;
            MinWidth   = 250;
            MinHeight  = 330;
            Background = WindowBg;
            // ReBuzz's main window as owner keeps this above the host and
            // makes it close with the application.
            try { Owner = Application.Current?.MainWindow; } catch { /* not fatal */ }

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(96) }); // loudness strip

            // ── Toolbar ──────────────────────────────────────────────────────
            var toolbar = new DockPanel { Margin = new Thickness(6, 4, 6, 2), LastChildFill = true };

            preBtn = new ToolbarButton("PRE", "Meter the inputs (PRE) or the post-fader signal (POST)");
            preBtn.Clicked += () => { showPre = !showPre; preBtn.Text = showPre ? "PRE" : "POST"; };
            DockPanel.SetDock(preBtn, Dock.Left);
            toolbar.Children.Add(preBtn);

            var clearBtn = new ToolbarButton("CLEAR", "Clear all peak holds and clip lights");
            clearBtn.Clicked += () => bridge?.ClearHolds();
            DockPanel.SetDock(clearBtn, Dock.Left);
            toolbar.Children.Add(clearBtn);

            // Three-line legend, right-aligned.
            var legend = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (string line in new[]
            {
                "solid = RMS (300 ms)",
                "translucent = peak",
                "master = true peak",
            })
            {
                legend.Children.Add(new TextBlock
                {
                    Text       = line,
                    FontFamily = Mono,
                    FontSize   = 9,
                    LineHeight = 11,
                    Foreground = FgDim,
                });
            }
            toolbar.Children.Add(legend);

            Grid.SetRow(toolbar, 0);
            rootGrid.Children.Add(toolbar);

            // ── Meter bridge ─────────────────────────────────────────────────
            bridge = new MeterBridge(imachine);
            bridge.MouseLeftButtonDown += (_, e) => { bridge.ClearHolds(); e.Handled = true; };
            Grid.SetRow(bridge, 1);
            rootGrid.Children.Add(bridge);

            // ── Loudness strip ───────────────────────────────────────────────
            var strip = new Grid { Background = StripBg };
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var stripButtons = new StackPanel { Margin = new Thickness(6, 6, 2, 6) };
            targetBtn = new ToolbarButton(TargetLabel(), "Switch the loudness target between -23 and -14 LUFS");
            targetBtn.Clicked += () =>
            {
                sharedTargetLufs = sharedTargetLufs == -23f ? -14f : -23f;
                targetBtn.Text   = TargetLabel();
                loudView.TargetLufs = sharedTargetLufs;
            };
            stripButtons.Children.Add(targetBtn);

            var resetBtn = new ToolbarButton("RESET", "Restart loudness integration");
            resetBtn.Clicked += () => { loudness.Reset(); firstReadDone = false; };
            resetBtn.Margin = new Thickness(0, 4, 0, 0);
            stripButtons.Children.Add(resetBtn);

            Grid.SetColumn(stripButtons, 0);
            strip.Children.Add(stripButtons);

            loudView = new LoudnessView { TargetLufs = sharedTargetLufs };
            Grid.SetColumn(loudView, 1);
            strip.Children.Add(loudView);

            Grid.SetRow(strip, 2);
            rootGrid.Children.Add(strip);

            Content = rootGrid;

            // ── Lifecycle ────────────────────────────────────────────────────
            clock.Start();
            lastFrameTime = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;

            if (imachine.Graph != null)
            {
                imachine.Graph.MachineRemoved += OnMachineRemoved;
                if (imachine.Graph.Buzz != null)
                    imachine.Graph.Buzz.PropertyChanged += OnBuzzPropertyChanged;
            }
            imachine.PropertyChanged += OnMachinePropertyChanged;

            Closed += (_, __) =>
            {
                // CompositionTarget.Rendering is STATIC — an un-removed
                // handler keeps this window alive and rendering forever.
                CompositionTarget.Rendering -= OnRendering;

                if (imachine.Graph != null)
                {
                    imachine.Graph.MachineRemoved -= OnMachineRemoved;
                    if (imachine.Graph.Buzz != null)
                        imachine.Graph.Buzz.PropertyChanged -= OnBuzzPropertyChanged;
                }
                imachine.PropertyChanged -= OnMachinePropertyChanged;

                Open.Remove(imachine);
            };
        }

        static string TargetLabel() => sharedTargetLufs == -23f ? "T -23" : "T -14";

        void OnMachineRemoved(IMachine m)
        {
            if (m == imachine) Close();
        }

        void OnBuzzPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // A new song means this machine instance is gone.
            if (e.PropertyName == "Song") Close();
        }

        void OnMachinePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "TrackCount")
                Dispatcher.BeginInvoke((Action)(() => bridge.SetChannelCount(imachine.TrackCount)));
        }

        // ── Render loop ──────────────────────────────────────────────────────

        void OnRendering(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;

            // Cap at ~60 fps. CompositionTarget.Rendering can fire more often
            // on high-refresh displays, and there's no value in metering faster
            // than the eye resolves.
            if (now - lastRenderTime < 1.0 / 62.0) return;
            lastRenderTime = now;

            float dt = (float)(now - lastFrameTime);
            lastFrameTime = now;

            var snap = machine.TakeMeters(1);
            if (snap == null) return;

            // The first read can hold peaks from long before the window
            // opened — discard it rather than slamming every meter.
            if (!firstReadDone) { firstReadDone = true; return; }

            bridge.Update(snap, showPre, dt, imachine);

            loudness.Update(snap.LufsBlocks,
                            snap.MsL, snap.MsR,
                            snap.CLL, snap.CLR, snap.CRR,
                            dt);

            loudView.Momentary   = loudness.MomentaryLufs;
            loudView.ShortTerm   = loudness.ShortTermLufs;
            loudView.Integrated  = loudness.IntegratedLufs;
            loudView.Correlation = loudness.Correlation;
            loudView.InvalidateVisual();
        }

        // ── Toolbar button ───────────────────────────────────────────────────

        sealed class ToolbarButton : Border
        {
            readonly TextBlock label;
            public event Action Clicked;

            public string Text { get => label.Text; set => label.Text = value; }

            public ToolbarButton(string text, string tip)
            {
                label = new TextBlock
                {
                    Text                = text,
                    FontFamily          = Mono,
                    FontSize            = 9,
                    Foreground          = FgNormal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                MinWidth        = 44;
                Height          = 17;
                Background      = MeterScale.Frozen(Color.FromRgb(44, 44, 50));
                BorderBrush     = MeterScale.Frozen(Color.FromRgb(70, 70, 80));
                BorderThickness = new Thickness(1);
                CornerRadius    = new CornerRadius(2);
                Cursor          = Cursors.Hand;
                ToolTip         = tip;
                Margin          = new Thickness(0, 0, 4, 0);
                Padding         = new Thickness(4, 0, 4, 0);
                Child           = label;

                MouseLeftButtonDown += (_, e) => { Clicked?.Invoke(); e.Handled = true; };
            }
        }
    }

    // ── Meter bridge ─────────────────────────────────────────────────────────
    //
    // Every column drawn in a single OnRender pass: channels then master L/R.
    // Keeping it to one FrameworkElement rather than a tree of Rectangles
    // matters at 60 fps with up to 26 columns.
    public sealed class MeterBridge : FrameworkElement
    {
        const double ScaleWidth   = 30;   // left dB ruler
        const double ReadoutBand  = 28;   // readouts above the bars
        const double LabelBand    = 26;   // labels below the bars
        const double ClipBoxH     = 8;
        const double MinColWidth  = 10;
        const double MaxColWidth  = 52;
        const double MasterGap    = 12;

        static readonly Brush TrackBg     = MeterScale.Frozen(Color.FromRgb(18, 18, 22));
        static readonly Brush HeadroomBg  = MeterScale.Frozen(Color.FromRgb(44, 24, 24));
        static readonly Brush MasterPanel = MeterScale.Frozen(Color.FromRgb(32, 32, 40));
        static readonly Brush Divider     = MeterScale.Frozen(Color.FromRgb(64, 64, 74));
        static readonly Brush TickBrush   = MeterScale.Frozen(Color.FromArgb(70, 200, 200, 210));
        static readonly Brush HoldBrush   = MeterScale.Frozen(Colors.White);
        static readonly Brush ClipOff     = MeterScale.Frozen(Color.FromRgb(46, 30, 30));
        static readonly Brush ClipOffEdge = MeterScale.Frozen(Color.FromArgb(90, 235, 60, 45));
        static readonly Brush ClipOn      = MeterScale.Frozen(Color.FromRgb(235, 60, 45));
        static readonly Brush FgLabel     = MeterScale.Frozen(Color.FromRgb(165, 165, 178));
        static readonly Brush FgLabelDim  = MeterScale.Frozen(Color.FromRgb(95, 95, 108));
        static readonly Brush FgScale     = MeterScale.Frozen(Color.FromRgb(110, 110, 124));
        static readonly Brush FgAmber     = MeterScale.Frozen(Color.FromRgb(235, 185, 60));
        static readonly Brush FgRed       = MeterScale.Frozen(Color.FromRgb(235, 80, 65));
        static readonly Brush BadgeSolo   = MeterScale.Frozen(Color.FromRgb(235, 185, 40));
        static readonly Brush BadgeMute   = MeterScale.Frozen(Color.FromRgb(215, 55, 45));
        static readonly FontFamily Mono   = new FontFamily("Consolas");
        static readonly Typeface  Face    = new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface  FaceB   = new Typeface(Mono, FontStyles.Normal, FontWeights.Bold,   FontStretches.Normal);

        // Scale ticks drawn over the bars. -50 is unlabelled (too tight).
        static readonly int[] Ticks       = { -50, -40, -30, -20, -12, -6, -3, 0 };
        static readonly bool[] TickLabels = { false, true, true, true, true, true, true, true };

        sealed class Col
        {
            public MeterBallistics Ball = new MeterBallistics();
            public float RmsDb = float.NegativeInfinity;
            public bool  Muted, Soloed, Audible = true;
            public string Label = "";
            public bool  IsMaster;
        }

        readonly List<Col> cols = new List<Col>();
        int channelCount;

        public MeterBridge(IMachine imachine)
        {
            SetChannelCount(imachine?.TrackCount ?? 0);
        }

        public void SetChannelCount(int n)
        {
            if (n < 1) n = 1;
            if (n > PedalGainMultiMachine.MaxTracks) n = PedalGainMultiMachine.MaxTracks;
            if (n == channelCount && cols.Count == n + 2) return;

            channelCount = n;
            cols.Clear();
            for (int i = 0; i < n; i++)
                cols.Add(new Col { Label = "In " + i });
            cols.Add(new Col { Label = "Out L", IsMaster = true });
            cols.Add(new Col { Label = "Out R", IsMaster = true });
            InvalidateVisual();
        }

        public void ClearHolds()
        {
            foreach (var c in cols) c.Ball.ClearHold();
            InvalidateVisual();
        }

        public void Update(MeterSnapshot snap, bool showPre, float dt, IMachine imachine)
        {
            if (snap == null) return;
            SetChannelCount(imachine?.TrackCount ?? channelCount);

            // Solo state decides which channels are actually heard.
            bool anySolo = false;
            for (int t = 0; t < channelCount; t++)
                if (ReadTrackParam(imachine, "Solo", t) != 0) { anySolo = true; break; }

            for (int t = 0; t < channelCount && t < cols.Count; t++)
            {
                var c = cols[t];
                float peak = showPre ? snap.PrePeak[t] : snap.PostPeak[t];
                float ms   = showPre ? snap.PreMS[t]   : snap.PostMS[t];
                c.Ball.Update(peak, dt);
                c.RmsDb  = MeterScale.MsToDb(ms);
                c.Muted  = ReadTrackParam(imachine, "Mute", t) != 0;
                c.Soloed = ReadTrackParam(imachine, "Solo", t) != 0;
                c.Audible = !c.Muted && (!anySolo || c.Soloed);
            }

            // Master pair — true peak, not sample peak.
            if (cols.Count >= 2)
            {
                var l = cols[cols.Count - 2];
                var r = cols[cols.Count - 1];
                l.Ball.Update(snap.TpL, dt);
                r.Ball.Update(snap.TpR, dt);
                l.RmsDb = MeterScale.MsToDb(snap.MsL);
                r.RmsDb = MeterScale.MsToDb(snap.MsR);
            }

            InvalidateVisual();
        }

        static int ReadTrackParam(IMachine m, string name, int track)
        {
            var groups = m?.ParameterGroups;
            if (groups == null) return 0;
            foreach (var g in groups)
            {
                if (g == null || g.Type != ParameterGroupType.Track || g.Parameters == null) continue;
                foreach (var p in g.Parameters)
                    if (p != null && p.Name == name)
                        return p.GetValue(track);
            }
            return 0;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0 || cols.Count == 0) return;

            // Clicks need something to hit.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double barTop    = ReadoutBand;
            double barBottom = h - LabelBand;
            double barH      = barBottom - barTop - ClipBoxH - 3;
            if (barH < 20) return;

            double availW = w - ScaleWidth - MasterGap - 6;
            double colW   = Math.Clamp(availW / cols.Count, MinColWidth, MaxColWidth);
            double gap    = Math.Min(4, colW * 0.18);
            double barW   = colW - gap;

            DrawScale(dc, barTop, barH);

            // Shaded panel behind the master pair.
            double masterX = ScaleWidth + channelCount * colW + MasterGap - gap * 0.5;
            dc.DrawRectangle(MasterPanel, null,
                new Rect(masterX - 3, barTop - 16, 2 * colW + 6, barBottom - barTop + 18));
            dc.DrawLine(new Pen(Divider, 1),
                new Point(masterX - 4, barTop - 16), new Point(masterX - 4, barBottom + 2));
            DrawText(dc, "MASTER", masterX, barTop - 15, 8, FgLabelDim, Face);

            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                double x = i < channelCount
                         ? ScaleWidth + i * colW
                         : ScaleWidth + channelCount * colW + MasterGap + (i - channelCount) * colW;

                DrawColumn(dc, c, x, barW, barTop, barH);
                DrawReadouts(dc, c, x, barW, barTop);
                DrawLabel(dc, c, x, barW, barBottom, i);
            }
        }

        void DrawScale(DrawingContext dc, double barTop, double barH)
        {
            for (int i = 0; i < Ticks.Length; i++)
            {
                if (!TickLabels[i]) continue;
                double y = barTop + (1.0 - MeterScale.Fraction(Ticks[i])) * barH;
                DrawText(dc, Ticks[i].ToString(CultureInfo.InvariantCulture),
                         2, y - 6, 8.5, FgScale, Face);
            }
        }

        void DrawColumn(DrawingContext dc, Col c, double x, double barW, double barTop, double barH)
        {
            double opacity = c.Audible ? 1.0 : 0.38;
            dc.PushOpacity(opacity);

            // Track, with the above-0 dBFS region tinted red.
            double zeroY = barTop + (1.0 - MeterScale.Fraction(0f)) * barH;
            dc.DrawRectangle(TrackBg, null, new Rect(x, barTop, barW, barH));
            dc.DrawRectangle(HeadroomBg, null, new Rect(x, barTop, barW, zeroY - barTop));

            // Peak — zone colours, translucent, drawn first.
            float peakDb = c.Ball.LevelDb;
            if (peakDb > MeterBallistics.MinDb)
            {
                double topY = barTop + (1.0 - MeterScale.Fraction(peakDb)) * barH;
                var col = MeterScale.ZoneColor(peakDb);
                var brush = new SolidColorBrush(Color.FromArgb(110, col.R, col.G, col.B));
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(x, topY, barW, barBottomOf(barTop, barH) - topY));
            }

            // RMS — solid, on top of the peak wash.
            float rmsDb = c.RmsDb;
            if (rmsDb > MeterBallistics.MinDb)
            {
                double topY = barTop + (1.0 - MeterScale.Fraction(rmsDb)) * barH;
                var brush = new SolidColorBrush(MeterScale.ZoneColor(rmsDb));
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(x, topY, barW, barBottomOf(barTop, barH) - topY));
            }

            // Scale ticks OVER the bars, so they stay readable when lit.
            var tickPen = new Pen(TickBrush, 0.7);
            foreach (int t in Ticks)
            {
                double y = barTop + (1.0 - MeterScale.Fraction(t)) * barH;
                dc.DrawLine(tickPen, new Point(x, y), new Point(x + barW, y));
            }

            // Held peak.
            if (c.Ball.HoldDb > MeterBallistics.MinDb + 0.5f)
            {
                double y = barTop + (1.0 - MeterScale.Fraction(c.Ball.HoldDb)) * barH;
                dc.DrawRectangle(HoldBrush, null, new Rect(x, y - 1, barW, 2));
            }

            // Clip box under the bar.
            double clipY = barTop + barH + 3;
            dc.DrawRectangle(c.Ball.Clipped ? ClipOn : ClipOff,
                             new Pen(c.Ball.Clipped ? ClipOn : ClipOffEdge, 1),
                             new Rect(x, clipY, barW, ClipBoxH));

            dc.Pop();
        }

        static double barBottomOf(double barTop, double barH) => barTop + barH;

        void DrawReadouts(DrawingContext dc, Col c, double x, double barW, double barTop)
        {
            // Held peak on top, RMS below it.
            float hold = c.Ball.HoldDb;
            Brush holdBrush = c.Ball.Clipped ? FgRed : (hold > -6f ? FgAmber : FgLabel);
            string holdText = hold <= MeterBallistics.MinDb + 0.5f
                ? "-∞"
                : (hold > 0f ? "+" : "") + hold.ToString("0.0", CultureInfo.InvariantCulture);
            DrawTextCentred(dc, holdText, x, barW, barTop - 26, 9,
                            hold <= MeterBallistics.MinDb + 0.5f ? FgLabelDim : holdBrush, Face);

            string rmsText = c.RmsDb <= MeterBallistics.MinDb ? "" :
                             c.RmsDb.ToString("0.0", CultureInfo.InvariantCulture);
            DrawTextCentred(dc, rmsText, x, barW, barTop - 14, 8.5, FgLabelDim, Face);
        }

        void DrawLabel(DrawingContext dc, Col c, double x, double barW, double barBottom, int index)
        {
            DrawTextCentred(dc, c.Label, x, barW, barBottom + 3, 8.5,
                            c.Audible ? FgLabel : FgLabelDim, Face);

            if (c.IsMaster) return;

            // M / S badges under muted or soloed channels.
            string badge = c.Muted ? "M" : (c.Soloed ? "S" : null);
            if (badge == null) return;
            var brush = c.Muted ? BadgeMute : BadgeSolo;
            double bw = 9, bh = 9;
            double bx = x + (barW - bw) * 0.5;
            double by = barBottom + 14;
            dc.DrawRectangle(brush, null, new Rect(bx, by, bw, bh));
            DrawTextCentred(dc, badge, bx, bw, by - 1, 8,
                            MeterScale.Frozen(Color.FromRgb(20, 20, 24)), FaceB);
        }

        static void DrawText(DrawingContext dc, string text, double x, double y,
                             double size, Brush brush, Typeface face)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                                       FlowDirection.LeftToRight, face, size, brush,
                                       VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
            dc.DrawText(ft, new Point(x, y));
        }

        static void DrawTextCentred(DrawingContext dc, string text, double x, double w,
                                    double y, double size, Brush brush, Typeface face)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                                       FlowDirection.LeftToRight, face, size, brush,
                                       VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
            dc.DrawText(ft, new Point(x + (w - ft.Width) * 0.5, y));
        }
    }

    // ── Loudness strip view ──────────────────────────────────────────────────
    //
    // Four rows: M and S bars on an LU scale relative to the target, the
    // integrated figure in LUFS with its offset from target, and the stereo
    // correlation bar.
    public sealed class LoudnessView : FrameworkElement
    {
        static readonly Brush BarBg     = MeterScale.Frozen(Color.FromRgb(20, 20, 25));
        static readonly Brush Below     = MeterScale.Frozen(Color.FromRgb(70, 130, 215));
        static readonly Brush Above     = MeterScale.Frozen(Color.FromRgb(230, 170, 55));
        static readonly Brush TargetPen = MeterScale.Frozen(Colors.White);
        static readonly Brush FgLabel   = MeterScale.Frozen(Color.FromRgb(165, 165, 178));
        static readonly Brush FgDim     = MeterScale.Frozen(Color.FromRgb(105, 105, 118));
        static readonly Brush CorrPos   = MeterScale.Frozen(Color.FromRgb(60, 190, 95));
        static readonly Brush CorrNeg   = MeterScale.Frozen(Color.FromRgb(225, 70, 55));
        static readonly FontFamily Mono = new FontFamily("Consolas");
        static readonly Typeface Face   = new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface FaceB  = new Typeface(Mono, FontStyles.Normal, FontWeights.Bold,   FontStretches.Normal);

        public float TargetLufs  { get; set; } = -23f;
        public float Momentary   { get; set; } = float.NegativeInfinity;
        public float ShortTerm   { get; set; } = float.NegativeInfinity;
        public float Integrated  { get; set; } = float.NegativeInfinity;
        public float Correlation { get; set; } = float.NaN;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 40 || h <= 40) return;

            const double labelW  = 16;
            const double valueW  = 66;
            double barX = labelW;
            double barW = w - labelW - valueW - 8;
            if (barW < 40) return;

            // Scale runs target-20 .. target+10 LU.
            float lo = TargetLufs - 20f, hi = TargetLufs + 10f;
            Func<float, double> pos = lufs =>
                Math.Clamp((lufs - lo) / (hi - lo), 0.0, 1.0) * barW;

            double rowH = 13, y = 4;

            DrawLufsRow(dc, "M", Momentary,  barX, barW, y, rowH, pos, valueW, w);
            y += rowH + 3;
            DrawLufsRow(dc, "S", ShortTerm,  barX, barW, y, rowH, pos, valueW, w);
            y += rowH + 2;

            // Scale labels -20 / -10 / 0 / +10 LU.
            foreach (var (lu, txt) in new[] { (-20f, "-20"), (-10f, "-10"), (0f, "0 LU"), (10f, "+10") })
            {
                double px = barX + pos(TargetLufs + lu);
                var ft = Text(txt, 8, FgDim, Face);
                dc.DrawText(ft, new Point(px - ft.Width * 0.5, y));
            }
            y += 12;

            // Integrated, bold, with its offset from target.
            string iText = float.IsNegativeInfinity(Integrated)
                ? "-∞ LUFS"
                : Integrated.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS";
            dc.DrawText(Text("I", 9, FgDim, Face), new Point(0, y + 2));
            dc.DrawText(Text(iText, 14, FgLabel, FaceB), new Point(labelW, y - 2));

            string offsetText = float.IsNegativeInfinity(Integrated)
                ? $"target {TargetLufs.ToString("0.0", CultureInfo.InvariantCulture)} LUFS"
                : (Integrated - TargetLufs >= 0 ? "+" : "")
                  + (Integrated - TargetLufs).ToString("0.0", CultureInfo.InvariantCulture)
                  + $" LU re {TargetLufs.ToString("0", CultureInfo.InvariantCulture)}";
            dc.DrawText(Text(offsetText, 9, FgDim, Face), new Point(labelW + 108, y + 4));
            y += 20;

            // Correlation: filled from the centre.
            dc.DrawText(Text("C", 9, FgDim, Face), new Point(0, y));
            double cx = barX, cw = barW, ch = 9;
            dc.DrawRectangle(BarBg, null, new Rect(cx, y, cw, ch));
            double mid = cx + cw * 0.5;
            if (!float.IsNaN(Correlation))
            {
                double half = cw * 0.5 * Math.Abs(Correlation);
                var brush = Correlation >= 0 ? CorrPos : CorrNeg;
                dc.DrawRectangle(brush, null, Correlation >= 0
                    ? new Rect(mid, y, half, ch)
                    : new Rect(mid - half, y, half, ch));
                double markerX = mid + cw * 0.5 * Correlation;
                dc.DrawRectangle(TargetPen, null, new Rect(markerX - 1, y - 1, 2, ch + 2));
            }
            dc.DrawRectangle(FgDim, null, new Rect(mid - 0.5, y, 1, ch));

            string cText = float.IsNaN(Correlation)
                ? "--"
                : Correlation.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
            dc.DrawText(Text(cText, 10, FgLabel, Face), new Point(cx + cw + 6, y - 2));
        }

        void DrawLufsRow(DrawingContext dc, string label, float value,
                         double barX, double barW, double y, double rowH,
                         Func<float, double> pos, double valueW, double totalW)
        {
            dc.DrawText(Text(label, 9, FgDim, Face), new Point(0, y + 1));
            dc.DrawRectangle(BarBg, null, new Rect(barX, y, barW, rowH - 3));

            if (!float.IsNegativeInfinity(value))
            {
                double px = pos(value);
                bool over = value > TargetLufs;
                dc.DrawRectangle(over ? Above : Below, null,
                                 new Rect(barX, y, px, rowH - 3));
            }

            // White target line.
            double tx = barX + pos(TargetLufs);
            dc.DrawRectangle(TargetPen, null, new Rect(tx - 1, y - 1, 2, rowH - 1));

            string txt = float.IsNegativeInfinity(value)
                ? "-∞"
                : value.ToString("0.0", CultureInfo.InvariantCulture);
            var ft = Text(txt, 9.5, FgLabel, Face);
            dc.DrawText(ft, new Point(barX + barW + 6, y - 1));
        }

        static FormattedText Text(string s, double size, Brush b, Typeface face) =>
            new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                              face, size, b,
                              VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
    }
}
