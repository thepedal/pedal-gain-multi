// Pedal Gain Multi — Machine GUI (code-only WPF, no XAML)
//
// Renders a compact meter stack at the top of the parameters window:
//
//   IN
//   [M][S] 1 ████████████░░░░░░░░  -12.3 dB
//   [M][S] 2 ██████░░░░░░░░░░░░░░  -18.1 dB
//   [M][S] 3 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [M][S] 4 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [M][S] 5 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [M][S] 6 ░░░░░░░░░░░░░░░░░░░░      -∞
//   ──────────────────────────────
//   [M]    OUT
//          L ██████████░░░░░░░░░░  -14.2 dB
//          R ██████████░░░░░░░░░░  -14.5 dB
//                     -48 -24 -12 -6 -3 0
//
// [M] mute buttons (red when on) toggle the Mute / Mute{N} switch
// parameters; [S] solo buttons (amber when on) toggle Solo{N} switch
// parameters. The per-input [M]s align vertically with the output [M]
// — all mutes stack in column 0. Per-input mutes ramp at the same rate
// as the output mute (set by the global Inertia parameter, 0–500 ms,
// default 25) and follow the DAW convention of mute beating solo.
//
// All parameter writes go through IParameter.SetValue so the GUI, params
// window, pattern editor, undo and save/load stay in sync.
//
// Meter ballistics run on the audio thread (see PedalGainMultiMachine.Work);
// this GUI just reads volatile values on a 33 ms timer.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMulti
{
    public class PedalGainMultiGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalGainMultiGUI();
    }

    public class PedalGainMultiGUI : UserControl, IMachineGUI
    {
        // Sized at compile time from the machine's NumInputs const.
        const int NumInputs = PedalGainMultiMachine.NumInputs;

        IMachine imachine;
        PedalGainMultiMachine machine;

        public IMachine Machine
        {
            get => imachine;
            set
            {
                imachine = value;
                machine  = value?.ManagedMachine as PedalGainMultiMachine;
            }
        }

        readonly DispatcherTimer timer;

        // Per-input widgets
        readonly Border[]    inMuteButtons = new Border[NumInputs];
        readonly TextBlock[] inMuteLabels  = new TextBlock[NumInputs];
        readonly Border[]    soloButtons   = new Border[NumInputs];
        readonly TextBlock[] soloLabels    = new TextBlock[NumInputs];
        readonly Rectangle[] inBars        = new Rectangle[NumInputs];
        readonly Rectangle[] inPeakLines   = new Rectangle[NumInputs];
        readonly TextBlock[] inDbTexts     = new TextBlock[NumInputs];
        readonly float[]     inHoldDb      = new float[NumInputs];
        readonly int[]       inHoldFrames  = new int[NumInputs];

        // Output widgets
        Rectangle barL,  barR;
        Rectangle peakL, peakR;
        TextBlock dbTextL, dbTextR;
        float holdDbL = DB_MIN, holdDbR = DB_MIN;
        int   holdFramesL,      holdFramesR;

        // Mute widgets (single button gating the whole output)
        Border    muteButton;
        TextBlock muteLabel;

        // ── Layout constants ─────────────────────────────────────────────────
        const float MUTE_W    = 14f;   // per-input M button column
        const float SOLO_W    = 14f;   // per-input S button column
        const float LABEL_W   = 14f;
        const float W         = 200f;
        const float READOUT_W = 46f;
        const float H         = 9f;
        const float DB_MIN    = -60f;
        const int   HOLD_FRAMES = 90;   // ~3 s at 33 ms/frame

        // ── Cached, frozen brushes ───────────────────────────────────────────
        static readonly Brush TrackBrush     = Freeze(new SolidColorBrush(Color.FromRgb(34,  34,  38)));
        static readonly Brush PeakBrush      = Freeze(new SolidColorBrush(Colors.White));
        static readonly Brush LabelColor     = Freeze(new SolidColorBrush(Color.FromRgb(170, 170, 180)));
        static readonly Brush ScaleColor     = Freeze(new SolidColorBrush(Color.FromRgb(95,  95, 105)));
        static readonly Brush SectionColor   = Freeze(new SolidColorBrush(Color.FromRgb(120, 120, 135)));
        static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(60,  60,  68)));

        // Solo button — off state uses the track background so it reads as a
        // "recessed" button; on state lights up amber with dark text.
        static readonly Brush SoloOffBg   = Freeze(new SolidColorBrush(Color.FromRgb(44,  44,  50)));
        static readonly Brush SoloOffFg   = Freeze(new SolidColorBrush(Color.FromRgb(130, 130, 140)));
        static readonly Brush SoloOnBg    = Freeze(new SolidColorBrush(Color.FromRgb(235, 185,  40)));
        static readonly Brush SoloOnFg    = Freeze(new SolidColorBrush(Color.FromRgb(20,  20,  25)));
        static readonly Brush SoloBorder  = Freeze(new SolidColorBrush(Color.FromRgb(70,  70,  80)));

        // Mute uses the same "off" recessed look but red when active —
        // standard DAW colour convention (solo = yellow, mute = red).
        static readonly Brush MuteOnBg    = Freeze(new SolidColorBrush(Color.FromRgb(215,  55,  45)));
        static readonly Brush MuteOnFg    = Freeze(new SolidColorBrush(Color.FromRgb(245, 245, 245)));

        static readonly FontFamily Mono = new FontFamily("Consolas");

        static Brush Freeze(Brush b) { b.Freeze(); return b; }

        // Green → yellow → red gradient with break points at -12 dB and -3 dB.
        static LinearGradientBrush LevelGradient()
        {
            float yp = Norm(-12f), rp = Norm(-3f);
            var b = new LinearGradientBrush
                { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            b.GradientStops.Add(new GradientStop(Color.FromRgb( 30, 175,  55), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb( 30, 175,  55), yp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(205, 185,   0), yp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(205, 185,   0), rp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(215,  45,  30), rp));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(215,  45,  30), 1.0));
            b.Freeze();
            return b;
        }

        // ── Construction ─────────────────────────────────────────────────────
        public PedalGainMultiGUI()
        {
            BuildUI();
            timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)   // ~30 fps
            };
            timer.Tick += Tick;
            timer.Start();
            Unloaded += (_, __) => timer.Stop();
        }

        // ── Layout helpers ───────────────────────────────────────────────────

        // Every row shares this 5-column grid:
        //   col 0 — per-input mute button [M] (empty on output L/R and scale rows;
        //           the output mute button also lives here on its own header row)
        //   col 1 — per-input solo button [S] (empty everywhere except input rows)
        //   col 2 — label
        //   col 3 — bar area (W px, the pixel-aligned meter column)
        //   col 4 — dB readout
        static Grid MakeRowGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MUTE_W)    });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SOLO_W)    });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LABEL_W)   });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W)         });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(READOUT_W) });
            return g;
        }

        void BuildUI()
        {
            var root = new StackPanel { Margin = new Thickness(6, 6, 6, 4) };
            var grad = LevelGradient();

            root.Children.Add(SectionHeader("IN"));

            for (int i = 0; i < NumInputs; i++)
            {
                var grid = MakeRowGrid();
                grid.Margin = new Thickness(0, 1, 0, 1);

                // Col 0 — per-input mute button.
                var (mbtn, mlbl) = MakeToggleButton(
                    letter:    "M",
                    tooltip:   $"Mute input {i + 1} (fade time = Inertia)",
                    paramName: $"Mute {i + 1}",
                    onBg:      MuteOnBg,
                    onFg:      MuteOnFg);
                Grid.SetColumn(mbtn, 0);
                grid.Children.Add(mbtn);
                inMuteButtons[i] = mbtn;
                inMuteLabels[i]  = mlbl;

                // Col 1 — solo button.
                var (sbtn, slbl) = MakeToggleButton(
                    letter:    "S",
                    tooltip:   $"Solo input {i + 1}",
                    paramName: $"Solo {i + 1}",
                    onBg:      SoloOnBg,
                    onFg:      SoloOnFg);
                Grid.SetColumn(sbtn, 1);
                grid.Children.Add(sbtn);
                soloButtons[i] = sbtn;
                soloLabels[i]  = slbl;

                // Col 2 — input number label.
                grid.Children.Add(RowLabel((i + 1).ToString(), col: 2));

                // Col 3 — bar canvas + peak-hold line.
                var (canvas, bar, peak) = BuildBarCanvas(grad);
                Grid.SetColumn(canvas, 3);
                grid.Children.Add(canvas);
                inBars[i]      = bar;
                inPeakLines[i] = peak;

                // Col 4 — dB readout.
                var db = RowReadout();
                Grid.SetColumn(db, 4);
                grid.Children.Add(db);
                inDbTexts[i] = db;

                inHoldDb[i]     = DB_MIN;
                inHoldFrames[i] = 0;

                root.Children.Add(grid);
            }

            // Thin separator — spans the bar area only for visual symmetry.
            root.Children.Add(new Rectangle
            {
                Height              = 1,
                Fill                = SeparatorBrush,
                Margin              = new Thickness(MUTE_W + SOLO_W + LABEL_W, 5, READOUT_W, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            // OUT header row: [M] mute button + "OUT" label, aligned to the
            // same 4-column grid as the input rows so the mute button sits
            // directly under the column of solo buttons above.
            root.Children.Add(BuildOutHeaderRow());

            (barL, peakL, dbTextL) = AddOutputRow(root, "L", grad);
            (barR, peakR, dbTextR) = AddOutputRow(root, "R", grad);

            root.Children.Add(MakeScaleRow());

            Content  = root;
            MinWidth = MUTE_W + SOLO_W + LABEL_W + W + READOUT_W + 12;
        }

        static UIElement SectionHeader(string text)
        {
            return new TextBlock
            {
                Text       = text,
                FontFamily = Mono,
                FontSize   = 8,
                Foreground = SectionColor,
                Margin     = new Thickness(0, 1, 0, 1)
            };
        }

        // OUT section header row: mute button in col 0 + "OUT" label in col 2.
        // The mute button stays in col 0 so it aligns vertically with the
        // column of per-input [M] buttons above — visually all the mutes
        // stack in the same column.
        UIElement BuildOutHeaderRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            var (btn, btnLbl) = MakeToggleButton(
                letter:    "M",
                tooltip:   "Mute output (fade time = Inertia parameter)",
                paramName: "Mute",
                onBg:      MuteOnBg,
                onFg:      MuteOnFg);
            Grid.SetColumn(btn, 0);
            grid.Children.Add(btn);
            muteButton = btn;
            muteLabel  = btnLbl;

            // The "OUT" label lives in col 2, styled to match the IN header.
            var hdr = new TextBlock
            {
                Text              = "OUT",
                FontFamily        = Mono,
                FontSize          = 8,
                Foreground        = SectionColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hdr, 2);
            grid.Children.Add(hdr);

            return grid;
        }

        static TextBlock RowLabel(string text, int col)
        {
            var lbl = new TextBlock
            {
                Text              = text,
                FontFamily        = Mono,
                FontSize          = 10,
                Foreground        = LabelColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, col);
            return lbl;
        }

        static TextBlock RowReadout() => new TextBlock
        {
            Text              = "-∞",
            Width             = READOUT_W - 2,
            TextAlignment     = TextAlignment.Right,
            FontFamily        = Mono,
            FontSize          = 10,
            Foreground        = LabelColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0)
        };

        // Build the bar canvas used by every meter row.
        static (Canvas canvas, Rectangle bar, Rectangle peak)
            BuildBarCanvas(Brush fill)
        {
            var canvas = new Canvas { Width = W, Height = H, ClipToBounds = true };

            var track = new Rectangle
            {
                Width = W, Height = H, Fill = TrackBrush, RadiusX = 1.5, RadiusY = 1.5
            };
            canvas.Children.Add(track);

            var bar = new Rectangle
            {
                Width = 0, Height = H, Fill = fill, RadiusX = 1.5, RadiusY = 1.5
            };
            Canvas.SetLeft(bar, 0);
            Canvas.SetTop(bar, 0);
            canvas.Children.Add(bar);

            var peak = new Rectangle
            {
                Width = 2, Height = H, Fill = PeakBrush, Opacity = 0
            };
            Canvas.SetTop(peak, 0);
            canvas.Children.Add(peak);

            return (canvas, bar, peak);
        }

        // Helper used for the output L / R rows — no toggle buttons, same
        // grid so the bar column still aligns pixel-for-pixel with the
        // input rows.
        (Rectangle bar, Rectangle peak, TextBlock db)
            AddOutputRow(Panel parent, string label, Brush fill)
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            grid.Children.Add(RowLabel(label, col: 2));

            var (canvas, bar, peak) = BuildBarCanvas(fill);
            Grid.SetColumn(canvas, 3);
            grid.Children.Add(canvas);

            var db = RowReadout();
            Grid.SetColumn(db, 4);
            grid.Children.Add(db);

            parent.Children.Add(grid);
            return (bar, peak, db);
        }

        // Scale row re-uses MakeRowGrid so column 3 matches the bar widths
        // pixel-for-pixel. Cols 0 / 1 / 2 / 4 stay empty.
        UIElement MakeScaleRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 2, 0, 0);

            var canvas = new Canvas { Width = W, Height = 11 };
            Grid.SetColumn(canvas, 3);

            int[] marks = { -48, -36, -24, -12, -6, -3, 0 };
            foreach (int db in marks)
            {
                var t = new TextBlock
                {
                    Text       = db.ToString(),
                    FontFamily = Mono,
                    FontSize   = 8,
                    Foreground = ScaleColor
                };
                // Rough centering offset by text length.
                double halfW = db <= -10 ? 7 : db < 0 ? 5 : 2;
                double x     = Norm(db) * W - halfW;
                Canvas.SetLeft(t, x);
                canvas.Children.Add(t);
            }

            grid.Children.Add(canvas);
            return grid;
        }

        // ── Toggle buttons (used by both solo and mute) ──────────────────────

        (Border border, TextBlock label)
            MakeToggleButton(string letter, string tooltip, string paramName, Brush onBg, Brush onFg)
        {
            var text = new TextBlock
            {
                Text                = letter,
                FontFamily          = Mono,
                FontSize            = 9,
                FontWeight          = FontWeights.Bold,
                Foreground          = SoloOffFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };

            var btn = new Border
            {
                // MUTE_W == SOLO_W, so the same button width works in both
                // columns. Two-pixel padding keeps the buttons visually
                // separated when they sit side-by-side in an input row.
                Width               = SOLO_W - 2,
                Height              = H,
                Background          = SoloOffBg,
                BorderBrush         = SoloBorder,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(2),
                Child               = text,
                Cursor              = Cursors.Hand,
                ToolTip             = tooltip,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                // Stash the on-colours on the button itself so the tick can
                // recolour without needing a parallel data structure.
                Tag                 = new ToggleColors(onBg, onFg)
            };

            btn.MouseLeftButtonDown += (_, e) =>
            {
                ToggleParameter(paramName);
                e.Handled = true;
            };

            return (btn, text);
        }

        // Holds the per-button "on" colours so RefreshToggleVisual can recolour
        // any button uniformly. Off colours are shared (SoloOffBg / SoloOffFg).
        sealed class ToggleColors
        {
            public readonly Brush OnBg, OnFg;
            public ToggleColors(Brush onBg, Brush onFg) { OnBg = onBg; OnFg = onFg; }
        }

        static void RefreshToggleVisual(Border btn, TextBlock label, bool on)
        {
            var c = (ToggleColors)btn.Tag;
            btn.Background   = on ? c.OnBg : SoloOffBg;
            label.Foreground = on ? c.OnFg : SoloOffFg;
        }

        // Flip a named bool parameter via the ReBuzz parameter API. Going
        // through IParameter.SetValue (rather than writing the property
        // directly) keeps the GUI, params window, pattern editor, undo and
        // save/load all consistent.
        void ToggleParameter(string name)
        {
            if (imachine?.ParameterGroups == null) return;

            foreach (var pg in imachine.ParameterGroups)
            {
                if (pg?.Parameters == null) continue;
                foreach (var p in pg.Parameters)
                {
                    if (p?.Name != name) continue;
                    int cur = p.GetValue(0);
                    p.SetValue(0, cur == 0 ? 1 : 0);
                    return;
                }
            }
        }

        // ── Meter math ───────────────────────────────────────────────────────

        // Map a dBFS value to a 0→1 position (used by both bars and scale).
        static float Norm(float db) =>
            Math.Clamp((db - DB_MIN) / -DB_MIN, 0f, 1f);

        static float LinToDb(float lin) =>
            lin < 1e-6f ? DB_MIN : 20f * MathF.Log10(lin);

        // Format a dB value for the meter readout. -∞ is shown when the
        // value is at or below the noise floor. Used to display the held
        // peak (UpdateHold tracks its value in dB, not linear).
        static string FormatDb(float db) =>
            db <= DB_MIN + 0.5f ? "-∞" : $"{db:F1}";

        void SetBar(Rectangle bar, float lin)
        {
            bar.Width = Math.Clamp(Norm(LinToDb(lin)) * W, 0f, W);
        }

        void UpdateHold(ref float holdDb, ref int frames, float currentDb, Rectangle line)
        {
            if (currentDb >= holdDb) { holdDb = currentDb; frames = 0; }
            else if (++frames > HOLD_FRAMES)
                holdDb = MathF.Max(holdDb - 0.4f, DB_MIN);

            Canvas.SetLeft(line, Math.Clamp(Norm(holdDb) * W - 1f, 0f, W - 2f));
            line.Opacity = holdDb > DB_MIN + 0.5f ? 1.0 : 0.0;
        }

        // ── Timer tick ───────────────────────────────────────────────────────
        void Tick(object sender, EventArgs e)
        {
            if (machine == null) return;

            // Per-input meters + solo / mute button state.
            for (int i = 0; i < NumInputs; i++)
            {
                float v = machine.MeterIn[i];
                SetBar(inBars[i], v);
                UpdateHold(ref inHoldDb[i], ref inHoldFrames[i], LinToDb(v), inPeakLines[i]);
                inDbTexts[i].Text = FormatDb(inHoldDb[i]);

                // Refresh toggle buttons — machine.Solo[] and machine.InMute[]
                // are the source of truth, updated by ReBuzz when the matching
                // parameter changes (via our click handler, the pattern editor,
                // or a song load).
                RefreshToggleVisual(inMuteButtons[i], inMuteLabels[i], machine.InMute[i]);
                RefreshToggleVisual(soloButtons[i],   soloLabels[i],   machine.Solo[i]);
            }

            // Mute button — same source-of-truth pattern.
            RefreshToggleVisual(muteButton, muteLabel, machine.Mute);

            // Stereo output meter — hold-synchronized readout.
            float l = machine.MeterL;
            float r = machine.MeterR;

            SetBar(barL, l);
            UpdateHold(ref holdDbL, ref holdFramesL, LinToDb(l), peakL);
            dbTextL.Text = FormatDb(holdDbL);

            SetBar(barR, r);
            UpdateHold(ref holdDbR, ref holdFramesR, LinToDb(r), peakR);
            dbTextR.Text = FormatDb(holdDbR);
        }
    }
}
