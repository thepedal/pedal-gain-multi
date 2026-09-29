// Pedal Gain Multi — parameter panel (v2.0.0)
//
// Code-only WPF (no XAML — avoids BAML loading issues in ReBuzz).
//
//   IN                                   PRE    VOL    PAN  MO
//   [M][S] 0 ████████░░░░░░  -12.3   [====|==]  [==|==] [MO]
//   [M][S] 1 ████░░░░░░░░░░  -18.1   [===|===]  [==|==] [MO]
//   ...                                   − +
//   [M] OUT (MASTER, TRUE PEAK)          [====|==]  [ METERS ]
//       L ██████████░░░░░░  -14.2
//       R ██████████░░░░░░  -14.5
//             -40 -30 -20 -12 -6 -3 0
//
// Everything is driven from the machine's parameters through
// IParameter.SetValue / GetValue, so undo, pattern automation and song
// saving all stay consistent — the panel never writes machine fields
// directly.
//
// Meters poll PedalGainMultiMachine.TakeMeters(0) on a DispatcherTimer.
// That polling is also what keeps the machine's analysis gate open (see
// the machine's "Analysis gating" section): a closed panel costs the
// audio thread almost nothing.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BuzzGUI.Interfaces;

namespace WDE.PedalGainMulti
{
    public class PedalGainMultiGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalGainMultiGUI();
    }

    public class PedalGainMultiGUI : UserControl, IMachineGUI
    {
        // ── Layout constants (DIP), from the handoff ─────────────────────────
        const double ColMute    = 14;
        const double ColSolo    = 14;
        const double ColNum     = 18;
        const double ColMeter   = 200;
        const double ColReadout = 46;
        const double ColVol     = 70;
        const double ColPan     = 38;
        const double ColMono    = 26;
        const double RowHeight  = 13;
        const double BarHeight  = 9;

        // ── Palette ──────────────────────────────────────────────────────────
        static readonly Brush BgTrack    = Frozen(Color.FromRgb(34, 34, 38));
        static readonly Brush BgButton   = Frozen(Color.FromRgb(44, 44, 50));
        static readonly Brush BorderCol  = Frozen(Color.FromRgb(70, 70, 80));
        static readonly Brush FgDim      = Frozen(Color.FromRgb(130, 130, 140));
        static readonly Brush FgNormal   = Frozen(Color.FromRgb(170, 170, 180));
        static readonly Brush FgSection  = Frozen(Color.FromRgb(120, 120, 135));
        static readonly Brush FgScale    = Frozen(Color.FromRgb(95, 95, 105));
        static readonly Brush Separator  = Frozen(Color.FromRgb(60, 60, 68));
        static readonly Brush SoloOnBg   = Frozen(Color.FromRgb(235, 185, 40));
        static readonly Brush SoloOnFg   = Frozen(Color.FromRgb(20, 20, 25));
        static readonly Brush MuteOnBg   = Frozen(Color.FromRgb(215, 55, 45));
        static readonly Brush MuteOnFg   = Frozen(Color.FromRgb(245, 245, 245));
        static readonly Brush MonoOnBg   = Frozen(Color.FromRgb(40, 170, 175));
        static readonly Brush MonoOnFg   = Frozen(Color.FromRgb(15, 25, 28));
        static readonly Brush HoldLine   = Frozen(Colors.White);
        static readonly Brush ClipRed    = Frozen(Color.FromRgb(235, 60, 45));
        static readonly Brush FaderTrack = Frozen(Color.FromRgb(28, 28, 32));
        static readonly Brush FaderKnob  = Frozen(Color.FromRgb(150, 150, 162));
        static readonly Brush FaderFill  = Frozen(Color.FromRgb(70, 90, 110));
        static readonly FontFamily Mono  = new FontFamily("Consolas");

        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // ── Machine binding ──────────────────────────────────────────────────
        IMachine imachine;
        PedalGainMultiMachine machine;

        public IMachine Machine
        {
            get => imachine;
            set
            {
                if (imachine != null)
                    imachine.PropertyChanged -= OnMachinePropertyChanged;

                imachine = value;
                machine  = value?.ManagedMachine as PedalGainMultiMachine;

                if (imachine != null)
                    imachine.PropertyChanged += OnMachinePropertyChanged;

                RebuildChannelRows();
            }
        }

        void OnMachinePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Track count changes mean a different number of channel strips.
            if (e.PropertyName == "TrackCount")
                Dispatcher.BeginInvoke((Action)RebuildChannelRows);
        }

        // ── Widgets ──────────────────────────────────────────────────────────
        StackPanel     root;
        StackPanel     channelHost;      // channel rows live here
        readonly List<ChannelRow> rows = new List<ChannelRow>();

        ToggleButtonLite masterMuteBtn;
        MiniFader        masterFader;
        ToggleButtonLite preFaderBtn;
        MeterBar         outMeterL, outMeterR;
        TextBlock        outReadoutL, outReadoutR;

        readonly MeterBallistics outBallL = new MeterBallistics();
        readonly MeterBallistics outBallR = new MeterBallistics();

        // PRE shows the raw input peaks, POST shows them after the strip.
        bool showPre;

        readonly DispatcherTimer timer;
        readonly Stopwatch       clock = new Stopwatch();
        double                   lastTick;
        bool                     firstReadDone;

        // ── Construction ─────────────────────────────────────────────────────
        public PedalGainMultiGUI()
        {
            BuildStaticUI();

            clock.Start();
            lastTick = clock.Elapsed.TotalSeconds;

            timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)   // ~30 fps
            };
            timer.Tick += OnTick;
            timer.Start();

            Unloaded += (_, __) => timer.Stop();
            Loaded   += (_, __) => { if (!timer.IsEnabled) timer.Start(); };
        }

        // ── Static layout ────────────────────────────────────────────────────

        void BuildStaticUI()
        {
            root = new StackPanel { Margin = new Thickness(6, 5, 6, 5) };

            root.Children.Add(BuildInHeader());

            channelHost = new StackPanel();
            root.Children.Add(channelHost);

            root.Children.Add(new Rectangle
            {
                Height = 1,
                Fill   = Separator,
                Margin = new Thickness(ColMute + ColSolo + ColNum, 5, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            root.Children.Add(BuildOutHeader());
            root.Children.Add(BuildOutRow(true));
            root.Children.Add(BuildOutRow(false));
            root.Children.Add(BuildScaleRow());

            Content  = root;
            MinWidth = ColMute + ColSolo + ColNum + ColMeter + ColReadout
                     + ColVol + ColPan + ColMono + 16;
        }

        // Shared 8-column grid so every row lines up exactly.
        static Grid NewRowGrid()
        {
            var g = new Grid();
            foreach (double w in new[] { ColMute, ColSolo, ColNum, ColMeter, ColReadout, ColVol, ColPan, ColMono })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            return g;
        }

        static TextBlock Label(string text, Brush fg, double size = 9, FontWeight? weight = null)
            => new TextBlock
            {
                Text              = text,
                FontFamily        = Mono,
                FontSize          = size,
                Foreground        = fg,
                FontWeight        = weight ?? FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };

        UIElement BuildInHeader()
        {
            var g = NewRowGrid();
            g.Margin = new Thickness(0, 0, 0, 2);

            var inLbl = Label("IN", FgSection, 8);
            Grid.SetColumn(inLbl, 0);
            Grid.SetColumnSpan(inLbl, 2);
            g.Children.Add(inLbl);

            // − / + track count buttons sit in the meter column, left-aligned.
            var trackBtns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Center
            };
            trackBtns.Children.Add(MakeTinyButton("−", "Remove a channel", () => NudgeTrackCount(-1)));
            trackBtns.Children.Add(MakeTinyButton("+", "Add a channel",    () => NudgeTrackCount(+1)));
            Grid.SetColumn(trackBtns, 3);
            g.Children.Add(trackBtns);

            // PRE / POST toggle in the readout column.
            preFaderBtn = new ToggleButtonLite("PRE", "Meter the input (PRE) or the post-fader signal (POST)")
            {
                Width = ColReadout - 4,
                OnBg  = BgButton,
                OnFg  = FgNormal
            };
            preFaderBtn.Clicked += () =>
            {
                showPre = !showPre;
                preFaderBtn.Text = showPre ? "PRE" : "POST";
            };
            preFaderBtn.Text = "PRE";
            showPre = true;
            Grid.SetColumn(preFaderBtn, 4);
            g.Children.Add(preFaderBtn);

            var volCap = Label("VOL", FgSection, 8);
            Grid.SetColumn(volCap, 5); g.Children.Add(volCap);
            var panCap = Label("PAN", FgSection, 8);
            Grid.SetColumn(panCap, 6); g.Children.Add(panCap);
            var moCap  = Label("MO", FgSection, 8);
            Grid.SetColumn(moCap, 7);  g.Children.Add(moCap);

            return g;
        }

        UIElement BuildOutHeader()
        {
            var g = NewRowGrid();
            g.Margin = new Thickness(0, 0, 0, 2);

            masterMuteBtn = new ToggleButtonLite("M", "Mute the master output")
            {
                OnBg = MuteOnBg, OnFg = MuteOnFg
            };
            masterMuteBtn.Clicked += () => ToggleGlobalSwitch("Master Mute");
            Grid.SetColumn(masterMuteBtn, 0);
            g.Children.Add(masterMuteBtn);

            var lbl = Label("OUT (MASTER, TRUE PEAK)", FgSection, 8);
            Grid.SetColumn(lbl, 2);
            Grid.SetColumnSpan(lbl, 3);
            g.Children.Add(lbl);

            masterFader = new MiniFader(isDbTaper: true)
            {
                Width   = ColVol - 4,
                ToolTip = "Master output level"
            };
            masterFader.ValueChanged += v => SetGlobal("Gain", v);
            Grid.SetColumn(masterFader, 5);
            g.Children.Add(masterFader);

            var metersBtn = MakeWideButton("METERS", "Open the big meter window", OpenMeterWindow);
            Grid.SetColumn(metersBtn, 6);
            Grid.SetColumnSpan(metersBtn, 2);
            g.Children.Add(metersBtn);

            return g;
        }

        UIElement BuildOutRow(bool left)
        {
            var g = NewRowGrid();
            g.Margin = new Thickness(0, 1, 0, 1);

            var lbl = Label(left ? "L" : "R", FgNormal, 9);
            Grid.SetColumn(lbl, 2);
            g.Children.Add(lbl);

            var bar = new MeterBar();
            Grid.SetColumn(bar, 3);
            g.Children.Add(bar);

            var readout = MakeReadout();
            Grid.SetColumn(readout, 4);
            g.Children.Add(readout);

            if (left) { outMeterL = bar; outReadoutL = readout; }
            else      { outMeterR = bar; outReadoutR = readout; }

            bar.MouseLeftButtonDown += (_, e) => { ClearAllHolds(); e.Handled = true; };
            return g;
        }

        UIElement BuildScaleRow()
        {
            var g = NewRowGrid();
            g.Margin = new Thickness(0, 2, 0, 0);

            var canvas = new Canvas { Width = ColMeter, Height = 10 };
            int[] marks = { -40, -30, -20, -12, -6, -3, 0 };
            foreach (int db in marks)
            {
                var t = new TextBlock
                {
                    Text       = db.ToString(CultureInfo.InvariantCulture),
                    FontFamily = Mono,
                    FontSize   = 7.5,
                    Foreground = FgScale
                };
                double halfW = db <= -10 ? 7 : (db < 0 ? 5 : 2);
                Canvas.SetLeft(t, MeterScale.PanelFraction(db) * ColMeter - halfW);
                canvas.Children.Add(t);
            }
            Grid.SetColumn(canvas, 3);
            g.Children.Add(canvas);
            return g;
        }

        static TextBlock MakeReadout() => new TextBlock
        {
            Text              = "-∞",
            Width             = ColReadout - 3,
            TextAlignment     = TextAlignment.Right,
            FontFamily        = Mono,
            FontSize          = 9,
            Foreground        = FgNormal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 3, 0)
        };

        Border MakeTinyButton(string text, string tip, Action onClick)
        {
            var b = new Border
            {
                Width               = 14,
                Height              = BarHeight,
                Background          = BgButton,
                BorderBrush         = BorderCol,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(2),
                Cursor              = Cursors.Hand,
                ToolTip             = tip,
                Margin              = new Thickness(0, 0, 3, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text                = text,
                    FontFamily          = Mono,
                    FontSize            = 9,
                    Foreground          = FgNormal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            };
            b.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
            return b;
        }

        Border MakeWideButton(string text, string tip, Action onClick)
        {
            var b = new Border
            {
                Height              = BarHeight,
                Background          = BgButton,
                BorderBrush         = BorderCol,
                BorderThickness     = new Thickness(1),
                CornerRadius        = new CornerRadius(2),
                Cursor              = Cursors.Hand,
                ToolTip             = tip,
                Margin              = new Thickness(2, 0, 0, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text                = text,
                    FontFamily          = Mono,
                    FontSize            = 7.5,
                    Foreground          = FgNormal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            };
            b.MouseLeftButtonDown += (_, e) => { onClick(); e.Handled = true; };
            return b;
        }

        // ── Channel rows ─────────────────────────────────────────────────────

        sealed class ChannelRow
        {
            public int              Track;
            public ToggleButtonLite Mute;
            public ToggleButtonLite Solo;
            public ToggleButtonLite MonoBtn;
            public MeterBar         Meter;
            public TextBlock        Readout;
            public MiniFader        Vol;
            public MiniFader        Pan;
            public MeterBallistics  Ball = new MeterBallistics();
        }

        void RebuildChannelRows()
        {
            if (channelHost == null) return;

            channelHost.Children.Clear();
            rows.Clear();

            // Core §5 warns that IMachine.TrackCount reads 0 for control
            // machines. This is an effect so it should be honest, but a 0
            // would leave the panel with no strips at all — fall back to the
            // default rather than render an empty mixer.
            int n = imachine?.TrackCount ?? 0;
            if (n < 1) n = PedalGainMultiMachine.DefTracks;
            if (n > PedalGainMultiMachine.MaxTracks) n = PedalGainMultiMachine.MaxTracks;

            for (int t = 0; t < n; t++)
                channelHost.Children.Add(BuildChannelRow(t));

            RefreshControlsFromParameters();
        }

        UIElement BuildChannelRow(int track)
        {
            var row = new ChannelRow { Track = track };
            var g   = NewRowGrid();
            g.Margin = new Thickness(0, 1, 0, 1);

            row.Mute = new ToggleButtonLite("M", $"Mute input {track}")
            {
                OnBg = MuteOnBg, OnFg = MuteOnFg
            };
            row.Mute.Clicked += () => ToggleTrackSwitch("Mute", track);
            Grid.SetColumn(row.Mute, 0);
            g.Children.Add(row.Mute);

            row.Solo = new ToggleButtonLite("S", $"Solo input {track}")
            {
                OnBg = SoloOnBg, OnFg = SoloOnFg
            };
            row.Solo.Clicked += () => ToggleTrackSwitch("Solo", track);
            Grid.SetColumn(row.Solo, 1);
            g.Children.Add(row.Solo);

            var num = Label(track.ToString(CultureInfo.InvariantCulture), FgNormal, 9);
            num.ToolTip = $"In {track} (track {track}) — its direct out is output plug {track + 1}";
            Grid.SetColumn(num, 2);
            g.Children.Add(num);

            row.Meter = new MeterBar();
            row.Meter.MouseLeftButtonDown += (_, e) => { ClearAllHolds(); e.Handled = true; };
            Grid.SetColumn(row.Meter, 3);
            g.Children.Add(row.Meter);

            row.Readout = MakeReadout();
            Grid.SetColumn(row.Readout, 4);
            g.Children.Add(row.Readout);

            row.Vol = new MiniFader(isDbTaper: true)
            {
                Width   = ColVol - 4,
                ToolTip = $"Level for input {track}"
            };
            int capturedTrack = track;
            row.Vol.ValueChanged += v => SetTrack("Volume", capturedTrack, v);
            Grid.SetColumn(row.Vol, 5);
            g.Children.Add(row.Vol);

            row.Pan = new MiniFader(isDbTaper: false)
            {
                Width    = ColPan - 4,
                MinValue = 0,
                MaxValue = 128,
                DefValue = 64,
                ToolTip  = $"Pan for input {track}"
            };
            row.Pan.ValueChanged += v => SetTrack("Pan", capturedTrack, v);
            Grid.SetColumn(row.Pan, 6);
            g.Children.Add(row.Pan);

            row.MonoBtn = new ToggleButtonLite("MO", $"Sum input {track} to mono")
            {
                OnBg = MonoOnBg, OnFg = MonoOnFg, Width = ColMono - 4
            };
            row.MonoBtn.Clicked += () => ToggleTrackSwitch("Mono", capturedTrack);
            Grid.SetColumn(row.MonoBtn, 7);
            g.Children.Add(row.MonoBtn);

            rows.Add(row);
            return g;
        }

        // ── Parameter access ─────────────────────────────────────────────────
        //
        // Always through IParameter so undo, patterns and saving stay
        // consistent. Parameters are looked up by group type + name rather
        // than by index, so adding a parameter later can't silently shift
        // which control drives what.

        IParameter FindParam(ParameterGroupType groupType, string name)
        {
            var groups = imachine?.ParameterGroups;
            if (groups == null) return null;
            foreach (var g in groups)
            {
                if (g == null || g.Type != groupType || g.Parameters == null) continue;
                foreach (var p in g.Parameters)
                    if (p != null && p.Name == name) return p;
            }
            return null;
        }

        IParameter GlobalParam(string name) => FindParam(ParameterGroupType.Global, name);
        IParameter TrackParam (string name) => FindParam(ParameterGroupType.Track,  name);

        void SetGlobal(string name, int value)
        {
            var p = GlobalParam(name);
            if (p != null) p.SetValue(0, value);
        }

        void SetTrack(string name, int track, int value)
        {
            var p = TrackParam(name);
            if (p != null) p.SetValue(track, value);
        }

        void ToggleGlobalSwitch(string name)
        {
            var p = GlobalParam(name);
            if (p == null) return;
            p.SetValue(0, p.GetValue(0) == 0 ? 1 : 0);
        }

        void ToggleTrackSwitch(string name, int track)
        {
            var p = TrackParam(name);
            if (p == null) return;
            p.SetValue(track, p.GetValue(track) == 0 ? 1 : 0);
        }

        int GetGlobal(string name, int fallback)
        {
            var p = GlobalParam(name);
            return p != null ? p.GetValue(0) : fallback;
        }

        int GetTrack(string name, int track, int fallback)
        {
            var p = TrackParam(name);
            return p != null ? p.GetValue(track) : fallback;
        }

        void NudgeTrackCount(int delta)
        {
            if (imachine == null) return;
            int n = Math.Clamp(imachine.TrackCount + delta, 1, PedalGainMultiMachine.MaxTracks);
            if (n != imachine.TrackCount) imachine.TrackCount = n;
        }

        // Pull every control's state back from the parameters. Cheap enough
        // to run every tick — it's a handful of dictionary reads — and means
        // pattern automation and undo are reflected without extra plumbing.
        void RefreshControlsFromParameters()
        {
            if (imachine == null) return;

            masterMuteBtn?.SetState(GetGlobal("Master Mute", 0) != 0);
            if (masterFader != null && !masterFader.IsDragging)
                masterFader.SetValueSilent(GetGlobal("Gain", 161));

            foreach (var r in rows)
            {
                r.Mute   ?.SetState(GetTrack("Mute", r.Track, 0) != 0);
                r.Solo   ?.SetState(GetTrack("Solo", r.Track, 0) != 0);
                r.MonoBtn?.SetState(GetTrack("Mono", r.Track, 0) != 0);

                if (r.Vol != null && !r.Vol.IsDragging)
                    r.Vol.SetValueSilent(GetTrack("Volume", r.Track, 161));
                if (r.Pan != null && !r.Pan.IsDragging)
                    r.Pan.SetValueSilent(GetTrack("Pan", r.Track, 64));
            }
        }

        // ── Meter polling ────────────────────────────────────────────────────

        void OnTick(object sender, EventArgs e)
        {
            if (machine == null) return;

            double now = clock.Elapsed.TotalSeconds;
            float  dt  = (float)(now - lastTick);
            lastTick = now;

            RefreshControlsFromParameters();

            var snap = machine.TakeMeters(0);
            if (snap == null) return;

            // The first read after the panel opens can carry peaks from long
            // before it was visible — discard it so the meters don't slam.
            if (!firstReadDone) { firstReadDone = true; return; }

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Track >= snap.PrePeak.Length) continue;

                float peak = showPre ? snap.PrePeak[r.Track] : snap.PostPeak[r.Track];
                r.Ball.Update(peak, dt);
                r.Meter.SetLevel(r.Ball.LevelDb, r.Ball.HoldDb, r.Ball.Clipped);
                r.Readout.Text       = FormatDb(r.Ball.HoldDb);
                r.Readout.Foreground = r.Ball.Clipped ? ClipRed : FgNormal;
            }

            // Master rows show TRUE peak, not sample peak.
            outBallL.Update(snap.TpL, dt);
            outBallR.Update(snap.TpR, dt);
            outMeterL.SetLevel(outBallL.LevelDb, outBallL.HoldDb, outBallL.Clipped);
            outMeterR.SetLevel(outBallR.LevelDb, outBallR.HoldDb, outBallR.Clipped);
            outReadoutL.Text       = FormatDb(outBallL.HoldDb);
            outReadoutR.Text       = FormatDb(outBallR.HoldDb);
            outReadoutL.Foreground = outBallL.Clipped ? ClipRed : FgNormal;
            outReadoutR.Foreground = outBallR.Clipped ? ClipRed : FgNormal;
        }

        void ClearAllHolds()
        {
            foreach (var r in rows) r.Ball.ClearHold();
            outBallL.ClearHold();
            outBallR.ClearHold();
        }

        static string FormatDb(float db)
        {
            if (db <= MeterBallistics.MinDb + 0.5f) return "-∞";
            string s = db.ToString("0.0", CultureInfo.InvariantCulture);
            return (db > 0f ? "+" : "") + s;
        }

        void OpenMeterWindow()
        {
            PedalGainMultiMeterWindow.ShowFor(imachine, machine);
        }

        // ── MeterBar ─────────────────────────────────────────────────────────
        //
        // One horizontal bar: track, gradient-filled level, white hold line,
        // red clip cap at the right end.
        sealed class MeterBar : Canvas
        {
            readonly Rectangle track;
            readonly Rectangle fill;
            readonly Rectangle hold;
            readonly Rectangle clip;

            public MeterBar()
            {
                Width  = ColMeter;
                Height = BarHeight;
                ClipToBounds = true;
                Background   = Brushes.Transparent;   // so clicks register

                track = new Rectangle
                {
                    Width = ColMeter, Height = BarHeight,
                    Fill  = BgTrack, RadiusX = 1.5, RadiusY = 1.5
                };
                Children.Add(track);

                // Absolute gradient over the FULL bar length — mapping it to
                // the lit portion instead is the v1.6.2 bug that made quiet
                // signals show red.
                fill = new Rectangle
                {
                    Width = 0, Height = BarHeight,
                    Fill  = MeterScale.CreateGradient(ColMeter),
                    RadiusX = 1.5, RadiusY = 1.5
                };
                SetLeft(fill, 0); SetTop(fill, 0);
                Children.Add(fill);

                hold = new Rectangle { Width = 2, Height = BarHeight, Fill = HoldLine, Opacity = 0 };
                SetTop(hold, 0);
                Children.Add(hold);

                clip = new Rectangle { Width = 3, Height = BarHeight, Fill = ClipRed, Opacity = 0 };
                SetLeft(clip, ColMeter - 3); SetTop(clip, 0);
                Children.Add(clip);
            }

            public void SetLevel(float levelDb, float holdDb, bool clipped)
            {
                fill.Width = Math.Clamp(MeterScale.PanelFraction(levelDb) * ColMeter, 0, ColMeter);

                if (holdDb > MeterBallistics.MinDb + 0.5f)
                {
                    SetLeft(hold, Math.Clamp(MeterScale.PanelFraction(holdDb) * ColMeter - 1, 0, ColMeter - 2));
                    hold.Opacity = 1;
                }
                else hold.Opacity = 0;

                clip.Opacity = clipped ? 1 : 0;
            }
        }

        // ── ToggleButtonLite ─────────────────────────────────────────────────
        //
        // A small latching button. State is always pushed in from the
        // parameters (SetState) rather than tracked locally, so pattern
        // automation and undo stay authoritative.
        sealed class ToggleButtonLite : Border
        {
            readonly TextBlock label;
            bool state;

            public Brush OnBg { get; set; } = Brushes.Orange;
            public Brush OnFg { get; set; } = Brushes.Black;

            public string Text
            {
                get => label.Text;
                set => label.Text = value;
            }

            public event Action Clicked;

            public ToggleButtonLite(string text, string tip)
            {
                label = new TextBlock
                {
                    Text                = text,
                    FontFamily          = Mono,
                    FontSize            = 8,
                    FontWeight          = FontWeights.Bold,
                    Foreground          = FgDim,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                };

                Width             = ColMute - 2;
                Height            = BarHeight;
                Background        = BgButton;
                BorderBrush       = BorderCol;
                BorderThickness   = new Thickness(1);
                CornerRadius      = new CornerRadius(2);
                Cursor            = Cursors.Hand;
                ToolTip           = tip;
                VerticalAlignment = VerticalAlignment.Center;
                HorizontalAlignment = HorizontalAlignment.Left;
                Child             = label;

                MouseLeftButtonDown += (_, e) => { Clicked?.Invoke(); e.Handled = true; };
            }

            public void SetState(bool on)
            {
                if (on == state) return;      // avoid pointless brush churn
                state = on;
                Background       = on ? OnBg : BgButton;
                label.Foreground = on ? OnFg : FgDim;
            }
        }

        // ── MiniFader ────────────────────────────────────────────────────────
        //
        // Compact horizontal fader. Drag to set, double-click for the default,
        // wheel for fine steps. dB faders use the console taper; others are
        // linear over [MinValue, MaxValue].
        sealed class MiniFader : Canvas
        {
            readonly Rectangle track;
            readonly Rectangle fill;
            readonly Rectangle knob;
            readonly bool      dbTaper;

            public int MinValue { get; set; } = 0;
            public int MaxValue { get; set; } = 181;
            public int DefValue { get; set; } = 161;

            int value = 161;
            public int Value => value;

            public bool IsDragging { get; private set; }
            public event Action<int> ValueChanged;

            public MiniFader(bool isDbTaper)
            {
                dbTaper = isDbTaper;
                Height  = BarHeight;
                ClipToBounds = true;
                Background   = Brushes.Transparent;

                track = new Rectangle { Height = BarHeight, Fill = FaderTrack, RadiusX = 1.5, RadiusY = 1.5 };
                SetLeft(track, 0); SetTop(track, 0);
                Children.Add(track);

                fill = new Rectangle { Height = BarHeight, Fill = FaderFill, RadiusX = 1.5, RadiusY = 1.5 };
                SetLeft(fill, 0); SetTop(fill, 0);
                Children.Add(fill);

                knob = new Rectangle { Width = 3, Height = BarHeight, Fill = FaderKnob };
                SetTop(knob, 0);
                Children.Add(knob);

                SizeChanged += (_, __) => { track.Width = ActualWidth; Redraw(); };
                // Canvas doesn't size its own children, and SizeChanged may
                // not have fired by first render — do an initial layout pass
                // once the element is actually in the tree.
                Loaded += (_, __) => { track.Width = ActualWidth; Redraw(); };

                MouseLeftButtonDown += OnDown;
                MouseMove           += OnMove;
                MouseLeftButtonUp   += OnUp;
                MouseWheel          += OnWheel;
            }

            void OnDown(object s, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2)
                {
                    Commit(DefValue);
                    e.Handled = true;
                    return;
                }
                IsDragging = true;
                CaptureMouse();
                ApplyFromX(e.GetPosition(this).X);
                e.Handled = true;
            }

            void OnMove(object s, MouseEventArgs e)
            {
                if (!IsDragging) return;
                ApplyFromX(e.GetPosition(this).X);
            }

            void OnUp(object s, MouseButtonEventArgs e)
            {
                if (!IsDragging) return;
                IsDragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }

            void OnWheel(object s, MouseWheelEventArgs e)
            {
                // dB faders step 2 codes (= 1 dB), or 1 code (0.5 dB) with Ctrl.
                // Linear faders step 5, or 1 with Ctrl.
                bool fine = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                int step  = dbTaper ? (fine ? 1 : 2) : (fine ? 1 : 5);
                Commit(value + (e.Delta > 0 ? step : -step));
                e.Handled = true;
            }

            void ApplyFromX(double x)
            {
                double w = ActualWidth;
                if (w <= 0) return;
                float travel = (float)Math.Clamp(x / w, 0.0, 1.0);
                Commit(dbTaper
                    ? FaderTaper.TravelToCode(travel)
                    : (int)Math.Round(MinValue + travel * (MaxValue - MinValue)));
            }

            void Commit(int v)
            {
                v = Math.Clamp(v, MinValue, MaxValue);
                if (v == value) return;
                value = v;
                Redraw();
                ValueChanged?.Invoke(v);
            }

            /// Push a value in without raising ValueChanged — used when
            /// refreshing from the parameters, so we don't write straight back.
            public void SetValueSilent(int v)
            {
                v = Math.Clamp(v, MinValue, MaxValue);
                if (v == value) return;
                value = v;
                Redraw();
            }

            void Redraw()
            {
                double w = ActualWidth;
                if (w <= 0) return;

                float travel = dbTaper
                    ? FaderTaper.CodeToTravel(value)
                    : (MaxValue > MinValue ? (value - MinValue) / (float)(MaxValue - MinValue) : 0f);

                double x = Math.Clamp(travel * w, 0, w);
                fill.Width = x;
                SetLeft(knob, Math.Clamp(x - 1.5, 0, w - 3));
            }
        }
    }
}
