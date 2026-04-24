// Pedal Gain Multi — Machine GUI (code-only WPF, no XAML)
//
// Renders a compact meter stack at the top of the parameters window:
//
//   IN
//   [S] 1 ████████████░░░░░░░░  -12.3 dB
//   [S] 2 ██████░░░░░░░░░░░░░░  -18.1 dB
//   [S] 3 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [S] 4 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [S] 5 ░░░░░░░░░░░░░░░░░░░░      -∞
//   [S] 6 ░░░░░░░░░░░░░░░░░░░░      -∞
//   ──────────────────────────────
//   OUT
//       L ██████████░░░░░░░░░░  -14.2 dB
//       R ██████████░░░░░░░░░░  -14.5 dB
//                  -48 -24 -12 -6 -3 0
//
// The [S] solo button for each input toggles the matching Solo{N} switch
// parameter via IParameter.SetValue — same path the pattern editor uses —
// so GUI, params window, and song state stay in sync.
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
        readonly Border[]    soloButtons  = new Border[NumInputs];
        readonly TextBlock[] soloLabels   = new TextBlock[NumInputs];
        readonly Rectangle[] inBars       = new Rectangle[NumInputs];
        readonly Rectangle[] inPeakLines  = new Rectangle[NumInputs];
        readonly TextBlock[] inDbTexts    = new TextBlock[NumInputs];
        readonly float[]     inHoldDb     = new float[NumInputs];
        readonly int[]       inHoldFrames = new int[NumInputs];

        // Output widgets
        Rectangle barL,  barR;
        Rectangle peakL, peakR;
        TextBlock dbTextL, dbTextR;
        float holdDbL = DB_MIN, holdDbR = DB_MIN;
        int   holdFramesL,      holdFramesR;

        // ── Layout constants ─────────────────────────────────────────────────
        const float SOLO_W    = 14f;
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

        // Every row shares this 4-column grid:
        //   col 0 — solo button (empty on output / scale rows)
        //   col 1 — label
        //   col 2 — bar area (W px, the pixel-aligned meter column)
        //   col 3 — dB readout
        static Grid MakeRowGrid()
        {
            var g = new Grid();
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

                // Col 0 — solo button.
                var (btn, btnLbl) = MakeSoloButton(i);
                Grid.SetColumn(btn, 0);
                grid.Children.Add(btn);
                soloButtons[i] = btn;
                soloLabels[i]  = btnLbl;

                // Col 1 — input number label.
                grid.Children.Add(RowLabel((i + 1).ToString(), col: 1));

                // Col 2 — bar canvas + peak-hold line.
                var (canvas, bar, peak) = BuildBarCanvas(grad);
                Grid.SetColumn(canvas, 2);
                grid.Children.Add(canvas);
                inBars[i]      = bar;
                inPeakLines[i] = peak;

                // Col 3 — dB readout.
                var db = RowReadout();
                Grid.SetColumn(db, 3);
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
                Margin              = new Thickness(SOLO_W + LABEL_W, 5, READOUT_W, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            root.Children.Add(SectionHeader("OUT"));

            (barL, peakL, dbTextL) = AddOutputRow(root, "L", grad);
            (barR, peakR, dbTextR) = AddOutputRow(root, "R", grad);

            root.Children.Add(MakeScaleRow());

            Content  = root;
            MinWidth = SOLO_W + LABEL_W + W + READOUT_W + 12;
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

        // Helper used for the output L / R rows — no solo column, same grid
        // so the bar column still aligns pixel-for-pixel with the input rows.
        (Rectangle bar, Rectangle peak, TextBlock db)
            AddOutputRow(Panel parent, string label, Brush fill)
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            grid.Children.Add(RowLabel(label, col: 1));

            var (canvas, bar, peak) = BuildBarCanvas(fill);
            Grid.SetColumn(canvas, 2);
            grid.Children.Add(canvas);

            var db = RowReadout();
            Grid.SetColumn(db, 3);
            grid.Children.Add(db);

            parent.Children.Add(grid);
            return (bar, peak, db);
        }

        // Scale row re-uses MakeRowGrid so column 2 matches the bar widths
        // pixel-for-pixel. Cols 0 / 1 / 3 stay empty.
        UIElement MakeScaleRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 2, 0, 0);

            var canvas = new Canvas { Width = W, Height = 11 };
            Grid.SetColumn(canvas, 2);

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

        // ── Solo button ──────────────────────────────────────────────────────

        (Border border, TextBlock label) MakeSoloButton(int inputIdx)
        {
            var text = new TextBlock
            {
                Text                = "S",
                FontFamily          = Mono,
                FontSize            = 9,
                FontWeight          = FontWeights.Bold,
                Foreground          = SoloOffFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };

            var btn = new Border
            {
                Width               = SOLO_W - 2,
                Height              = H,
                Background          = SoloOffBg,
                BorderBrush         = SoloBorder,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(2),
                Child               = text,
                Cursor              = Cursors.Hand,
                ToolTip             = $"Solo input {inputIdx + 1}",
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            btn.MouseLeftButtonDown += (_, e) =>
            {
                ToggleSolo(inputIdx);
                e.Handled = true;
            };

            return (btn, text);
        }

        // Flip the matching Solo{N} parameter via the ReBuzz parameter API.
        // Going through IParameter.SetValue (instead of writing the property
        // directly) keeps pattern editor, params window, undo and save/load
        // all consistent with the GUI.
        void ToggleSolo(int i)
        {
            if (imachine?.ParameterGroups == null) return;

            string target = $"Solo {i + 1}";
            foreach (var pg in imachine.ParameterGroups)
            {
                if (pg?.Parameters == null) continue;
                foreach (var p in pg.Parameters)
                {
                    if (p?.Name != target) continue;
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

            // Per-input meters + solo button state.
            for (int i = 0; i < NumInputs; i++)
            {
                float v = machine.MeterIn[i];
                SetBar(inBars[i], v);
                UpdateHold(ref inHoldDb[i], ref inHoldFrames[i], LinToDb(v), inPeakLines[i]);
                inDbTexts[i].Text = FormatDb(inHoldDb[i]);

                // Refresh solo button visual — machine.Solo[] is the source of
                // truth, updated by ReBuzz when the parameter changes (either
                // via our click handler, the pattern editor, or a song load).
                bool on = machine.Solo[i];
                soloButtons[i].Background = on ? SoloOnBg : SoloOffBg;
                soloLabels[i].Foreground  = on ? SoloOnFg : SoloOffFg;
            }

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
