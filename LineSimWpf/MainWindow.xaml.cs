using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LineSimWpf
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<Stage> Stages { get; } = new();

        private DispatcherTimer _timer;
        private double _simTime;                         // минуты «на часах»
        private bool _running;
        private List<JobStage> _plan = new();

        // события: завершение каждого этапа = (время завершения, jobId, stageIndex)
        private List<(double time, int jobId, int stageIndex)> _events = new();
        private int _evtPtr = 0;

        // геометрия «виртуального полотна» (масштабируется Viewbox'ом)
        const double XMargin = 60;
        const double Gap = 16;
        const double StageW = 160;
        const double LaneH = 54;
        const double HeadTop = 28;
        const double VGap = 8;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            StagesGrid.ItemsSource = Stages;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += _timer_Tick;

            Stages.CollectionChanged += Stages_CollectionChanged;
            SizeChanged += (_, __) => RedrawStatic();
            ModeBox.SelectionChanged += (_, __) => BuildPlanAndReset();
            ShiftBox.TextChanged += (_, __) => BuildPlanAndReset();
            HorizonBox.TextChanged += (_, __) => BuildPlanAndReset();

            BuildPlanAndReset();
        }

        // ——— helpers ———
        private double ShiftMinutes() =>
            double.TryParse(ShiftBox.Text, out var v) ? v : 720;

        private int NumShifts()
        {
            if (int.TryParse(HorizonBox.Text, out var k) && k > 0) return k;
            if (double.TryParse(HorizonBox.Text, out var kd) && kd > 0) return Math.Max(1, (int)Math.Floor(kd));
            return 1;
        }
        private double HorizonMinutes() => ShiftMinutes() * NumShifts();

        private TimeMode CurrentMode()
        {
            var item = (ModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            return item switch
            {
                "Min" => TimeMode.Min,
                "Max" => TimeMode.Max,
                _ => TimeMode.RandomEachJob
            };
        }

        // ——— reactions ———
        private void Stages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (Stage s in e.NewItems) s.PropertyChanged += Stage_PropertyChanged;
            if (e.OldItems != null)
                foreach (Stage s in e.OldItems) s.PropertyChanged -= Stage_PropertyChanged;

            BuildPlanAndReset();
        }

        private void Stage_PropertyChanged(object? sender, PropertyChangedEventArgs e) => BuildPlanAndReset();

        // ——— core: строим по-сменам БЕЗ переноса WIP ———
        private void BuildPlanAndReset()
        {
            if (Stages.Count == 0)
            {
                _plan.Clear(); _events.Clear(); _evtPtr = 0; _simTime = 0;
                OutputBlock.Text = "0";
                TimeBlock.Text = "t = 0.0 мин";
                PerShiftList?.Items.Clear();
                LogList?.Items.Clear();
                RedrawStatic();
                return;
            }

            int shifts = Math.Max(1, NumShifts());
            double shift = ShiftMinutes();

            var bigPlan = new List<JobStage>();
            var perShiftCounts = new List<int>();
            int jobIdOffset = 0;

            for (int i = 0; i < shifts; i++)
            {
                // моделируем КАЖДУЮ смену отдельно, без переноса WIP
                var (plan, completed) = Scheduler.BuildSchedule(
                    Stages.ToList(),
                    shift,
                    CurrentMode(),
                    maxJobs: 200000,
                    planHorizonMinutes: shift,  // равен смене
                    continuousFeed: false,      // ОТСЕЧКА: никто не переносится
                    seed: 12345 + i);

                perShiftCounts.Add(completed);

                double tOffset = i * shift;

                // сдвигаем времена и jobId, чтобы склеить план по времени
                int maxJobIdThis = plan.Any() ? plan.Max(p => p.JobId) + 1 : 0;
                foreach (var js in plan)
                    bigPlan.Add(new JobStage(js.JobId + jobIdOffset, js.StageIndex, js.CenterIndex,
                                             js.Start + tOffset, js.Finish + tOffset));
                jobIdOffset += maxJobIdThis;
            }

            _plan = bigPlan;
            _simTime = 0;
            _evtPtr = 0;

            _events = _plan
                .Select(js => (time: js.Finish, jobId: js.JobId, stageIndex: js.StageIndex))
                .OrderBy(e => e.time)
                .ToList();

            // сводка: готово за 1-ю смену
            OutputBlock.Text = (perShiftCounts.Count > 0 ? perShiftCounts[0] : 0).ToString();
            TimeBlock.Text = "t = 0.0 мин";

            // готово по сменам (по последнему этапу) — берём из perShiftCounts
            PerShiftList?.Items.Clear();
            for (int i = 0; i < perShiftCounts.Count; i++)
                PerShiftList.Items.Add($"Смена {i + 1}: {perShiftCounts[i]}");

            LogList?.Items.Clear();
            RedrawStatic();
            RedrawDynamic();
        }

        // ——— timer ———
        private void _timer_Tick(object? sender, EventArgs e)
        {
            double speed = Math.Max(0.1, SpeedSlider.Value);
            _simTime += 0.03 * speed * 60.0 / 60.0; // минуты

            while (_evtPtr < _events.Count && _events[_evtPtr].time <= _simTime)
            {
                var (time, jobId, stageIndex) = _events[_evtPtr];
                string stageName = (stageIndex >= 0 && stageIndex < Stages.Count)
                    ? Stages[stageIndex].Name
                    : $"Этап {stageIndex + 1}";
                LogList.Items.Insert(0, $"t={time:F1} мин: Машина #{jobId + 1} прошла {stageName}");
                _evtPtr++;
                if (LogList.Items.Count > 400) LogList.Items.RemoveAt(LogList.Items.Count - 1);
            }

            if (Stages.Count == 0) _running = false;

            TimeBlock.Text = $"t = {_simTime:F1} мин";
            RedrawDynamic();

            // теперь бежим только в пределах выбранного горизонта (N смен)
            if (_simTime > HorizonMinutes() + 1) _running = false;
            if (!_running) _timer.Stop();
        }

        // ——— UI actions ———
        private void StartPause_Click(object sender, RoutedEventArgs e)
        {
            if (Stages.Count == 0) return;
            if (_running) { _running = false; _timer.Stop(); }
            else { _running = true; _timer.Start(); }
        }

        private void Reset_Click(object sender, RoutedEventArgs e) => BuildPlanAndReset();

        private void AddStageBtn_Click(object sender, RoutedEventArgs e)
        {
            var stage = new Stage { Name = "Этап", MinMinutes = 10, MaxMinutes = 15, Centers = 1 };
            Stages.Add(stage);
            StagesGrid.SelectedItem = stage;
            StagesGrid.ScrollIntoView(stage);
        }

        private void RemoveStageBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Stages.Any())
            {
                if (StagesGrid.SelectedItem is Stage s) Stages.Remove(s);
                else Stages.RemoveAt(Stages.Count - 1);
            }
        }

        // ——— drawing (static grid) ———
        private void RedrawStatic()
        {
            var canvas = StageCanvas;
            canvas.Children.Clear();

            if (Stages.Count == 0)
            {
                canvas.Width = 400;
                canvas.Height = 200;
                return;
            }

            int n = Stages.Count;
            int lanes = Stages.Sum(s => s.Centers);

            double contentW = XMargin * 2 + n * StageW + (n - 1) * Gap;
            double contentH = HeadTop + lanes * (LaneH + VGap) + 10;

            canvas.Width = contentW;
            canvas.Height = contentH;

            for (int s = 0; s < n; s++)
            {
                double x = XMargin + s * (StageW + Gap);
                var tb = new TextBlock
                {
                    Text = $"{Stages[s].Name} ({Stages[s].Centers}п)",
                    Foreground = Brushes.Black,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold
                };
                Canvas.SetLeft(tb, x);
                Canvas.SetTop(tb, 6);
                canvas.Children.Add(tb);
            }

            double y = HeadTop;
            int stageIdx = 0;
            foreach (var st in Stages)
            {
                for (int c = 0; c < st.Centers; c++)
                {
                    double x = XMargin + stageIdx * (StageW + Gap);
                    var r = new Rectangle
                    {
                        Width = StageW,
                        Height = LaneH,
                        RadiusX = 8,
                        RadiusY = 8,
                        Stroke = new SolidColorBrush(Color.FromRgb(54, 95, 160)),
                        Fill = new SolidColorBrush(Color.FromRgb(236, 245, 255)),
                        StrokeThickness = 1.5
                    };
                    Canvas.SetLeft(r, x);
                    Canvas.SetTop(r, y);
                    canvas.Children.Add(r);

                    var lb = new TextBlock
                    {
                        Text = $"#{c + 1}",
                        Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                        FontSize = 11
                    };
                    Canvas.SetLeft(lb, x + 8);
                    Canvas.SetTop(lb, y + 4);
                    canvas.Children.Add(lb);

                    y += LaneH + VGap;
                }
                stageIdx++;
            }
        }

        // ——— drawing (active jobs) ———
        private void RedrawDynamic()
        {
            if (Stages.Count == 0) { StageCanvas.Children.Clear(); return; }

            RedrawStatic();

            var laneYs = new List<double>();
            double y = HeadTop;
            foreach (var st in Stages)
            {
                for (int c = 0; c < st.Centers; c++)
                {
                    laneYs.Add(y);
                    y += LaneH + VGap;
                }
            }

            foreach (var js in _plan)
            {
                if (_simTime < js.Start || _simTime > js.Finish) continue;

                double progress = Math.Clamp((_simTime - js.Start) / (js.Finish - js.Start), 0, 1);
                double x0 = XMargin + js.StageIndex * (StageW + Gap) + 10;
                double x1 = XMargin + js.StageIndex * (StageW + Gap) + StageW - 28;
                double x = x0 + (x1 - x0) * progress;

                int laneIndex = Stages.Take(js.StageIndex).Sum(s => s.Centers) + js.CenterIndex;
                double yLane = laneYs[laneIndex];

                var car = new Ellipse
                {
                    Width = LaneH * 0.72,
                    Height = LaneH * 0.72,
                    Fill = new SolidColorBrush(Color.FromRgb(255, 92, 53)),
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.0
                };
                Canvas.SetLeft(car, x);
                Canvas.SetTop(car, yLane + (LaneH - car.Height) / 2);
                StageCanvas.Children.Add(car);
            }
        }
    }
}