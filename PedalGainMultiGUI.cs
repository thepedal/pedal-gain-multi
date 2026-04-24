// Pedal Gain Multi — Machine GUI (code-only WPF, no XAML)
//
// Renders a compact meter stack at the top of the parameters window:
//
//   IN
//   1 ████████████░░░░░░░░  -12.3 dB
//   2 ██████░░░░░░░░░░░░░░  -18.1 dB
//   3 ░░░░░░░░░░░░░░░░░░░░      -∞
//   4 ░░░░░░░░░░░░░░░░░░░░      -∞
//   5 ░░░░░░░░░░░░░░░░░░░░      -∞
//   6 ░░░░░░░░░░░░░░░░░░░░      -∞
//   ──────────────────────────────
//   OUT
//   L ██████████░░░░░░░░░░  -14.2 dB
//   R ██████████░░░░░░░░░░  -14.5 dB
//            -48 -24 -12 -6 -3 0
//
// Meter ballistics run on the audio thread (see PedalGainMultiMachine.Work).
// The UI just reads the latest normalized values on a 33 ms timer and moves
// a handful of Rectangles — no cross-thread marshaling, no per-frame allocs.

using System;
using System.Windows;
using System.Windows.Controls;
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
        // Re-declare as a local const so arrays here are sized at compile time.
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

        // Per-input meter widgets
        readonly Rectangle[] inBars       = new Rectangle[NumInputs];
        readonly Rectangle[] inPeakLines  = new Rectangle[NumInputs];
        readonly TextBlock[] inDbTexts    = new TextBlock[NumInputs];
        readonly float[]     inHoldDb     = new float[NumInputs];
        readonly int[]       inHoldFrames = new int[NumInputs];

        // Stereo output meter widgets
        Rectangle barL,  barR;
        Rectangle peakL, peakR;
        TextBlock dbTextL, dbTextR;
        float holdDbL = DB_MIN, holdDbR = DB_MIN;
        int   holdFramesL,      holdFramesR;

        // ── Layout constants ─────────────────────────────────────────────────
        const float W         = 200f;
        const float H         = 9f;
        const float LABEL_W   = 20f;
        const float READOUT_W = 46f;
        const float DB_MIN    = -60f;
        const int   HOLD_FRAMES = 90;   // ~3 s at 33 ms/frame

        // ── Cached, frozen brushes ───────────────────────────────────────────
        static readonly Brush TrackBrush     = Freeze(new SolidColorBrush(Color.FromRgb(34,  34,  38)));
        static readonly Brush PeakBrush      = Freeze(new SolidColorBrush(Colors.White));
        static readonly Brush LabelColor     = Freeze(new SolidColorBrush(Color.FromRgb(170, 170, 180)));
        static readonly Brush ScaleColor     = Freeze(new SolidColorBrush(Color.FromRgb(95,  95, 105)));
        static readonly Brush SectionColor   = Freeze(new SolidColorBrush(Color.FromRgb(120, 120, 135)));
        static readonly Brush SeparatorBrush = Freeze(new SolidColorBrush(Color.FromRgb(60,  60,  68)));
        static readonly FontFamily Mono      = new FontFamily("Consolas");

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

        // Every meter row uses the same 3-column grid so column 1 is always
        // the bar area — pixel-perfect alignment across inputs, outputs,
        // and the scale row.
        static Grid MakeRowGrid()
        {
            var g = new Grid();
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
                (inBars[i], inPeakLines[i], inDbTexts[i]) =
                    AddMeterRow(root, (i + 1).ToString(), grad);
                inHoldDb[i]     = DB_MIN;
                inHoldFrames[i] = 0;
            }

            // Thin separator line spanning the bar area only.
            var sep = new Rectangle
            {
                Height = 1,
                Fill   = SeparatorBrush,
                Margin = new Thickness(LABEL_W, 5, READOUT_W, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            root.Children.Add(sep);

            root.Children.Add(SectionHeader("OUT"));

            (barL, peakL, dbTextL) = AddMeterRow(root, "L", grad);
            (barR, peakR, dbTextR) = AddMeterRow(root, "R", grad);

            root.Children.Add(MakeScaleRow());

            Content  = root;
            MinWidth = LABEL_W + W + READOUT_W + 12;
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

        (Rectangle bar, Rectangle peak, TextBlock db)
            AddMeterRow(Panel parent, string label, Brush fill)
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 1, 0, 1);

            // Label column
            var lbl = new TextBlock
            {
                Text              = label,
                FontFamily        = Mono,
                FontSize          = 10,
                Foreground        = LabelColor,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            // Bar canvas (track + fill + peak-hold line)
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

            Grid.SetColumn(canvas, 1);
            grid.Children.Add(canvas);

            // dB readout
            var db = new TextBlock
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
            Grid.SetColumn(db, 2);
            grid.Children.Add(db);

            parent.Children.Add(grid);
            return (bar, peak, db);
        }

        // Scale row re-uses MakeRowGrid so column 1 matches the bar widths
        // pixel-for-pixel.
        UIElement MakeScaleRow()
        {
            var grid = MakeRowGrid();
            grid.Margin = new Thickness(0, 2, 0, 0);

            var canvas = new Canvas { Width = W, Height = 11 };
            Grid.SetColumn(canvas, 1);

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

            // Per-input meters. The dB readout tracks the *held* peak (same
            // value that drives the white peak line) so the number pauses
            // alongside the line instead of racing the falling bar.
            for (int i = 0; i < NumInputs; i++)
            {
                float v = machine.MeterIn[i];
                SetBar(inBars[i], v);
                UpdateHold(ref inHoldDb[i], ref inHoldFrames[i], LinToDb(v), inPeakLines[i]);
                inDbTexts[i].Text = FormatDb(inHoldDb[i]);
            }

            // Stereo output meter — same hold-synchronized readout.
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
