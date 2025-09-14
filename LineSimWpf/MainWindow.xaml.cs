using System;
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
        private double _simTime;
        private bool _running;

        private System.Collections.Generic.List<JobStage> _plan = new();
        private System.Collections.Generic.List<(double time, int jobId, int stageIndex)> _events = new();
        private int _evtPtr = 0;

        // геометрия «виртуального полотна»
        const double XMargin = 60;      // левый/правый отступ
        const double Gap = 16;      // горизонтальный зазор между этапами
        const double StageW = 160;     // базовая ширина «станции»
        const double LaneH = 54;      // базовая высота полосы (поста)
        const double HeadTop = 28;      // высота области заголовков
        const double VGap = 8;       // вертикальный зазор между полосами

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            StagesGrid.ItemsSource = Stages;
            // Стартуем БЕЗ этапов (пустая схема)

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += _timer_Tick;

            // Авто-пересчёт плана и схемы при любых изменениях
            Stages.CollectionChanged += Stages_CollectionChanged;
            SizeChanged += (_, __) => RedrawStatic();
            ModeBox.SelectionChanged += (_, __) => BuildPlanAndReset();
            ShiftBox.TextChanged += (_, __) => BuildPlanAndReset();

            BuildPlanAndReset(); // покажем пустую схему/нулевые метрики
        }

        private void Stages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (Stage s in e.NewItems) s.PropertyChanged += Stage_PropertyChanged;
            if (e.OldItems != null)
                foreach (Stage s in e.OldItems) s.PropertyChanged -= Stage_PropertyChanged;

            BuildPlanAndReset();
        }

        private void Stage_PropertyChanged(object? sender, PropertyChangedEventArgs e) => BuildPlanAndReset();

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

        private double ShiftMinutes() =>
            double.TryParse(ShiftBox.Text, out var v) ? v : 720;

        private void BuildPlanAndReset()
        {
            // если этапов нет — просто очистим визуал и метрики
            if (Stages.Count == 0)
            {
                _plan.Clear();
                _events.Clear();
                _evtPtr = 0;
                _simTime = 0;
                OutputBlock.Text = "0";
                TimeBlock.Text = "t = 0.0 мин";
                LogList?.Items.Clear();
                RedrawStatic();  // пустая сетка
                return;
            }

            var (plan, _) = Scheduler.BuildSchedule(
                Stages.ToList(), ShiftMinutes(), CurrentMode(), maxJobs: 5000);

            _plan = plan;
            _simTime = 0;
            _evtPtr = 0;

            _events = _plan
                .Select(js => (time: js.Finish, jobId: js.JobId, stageIndex: js.StageIndex))
                .OrderBy(e => e.time)
                .ToList();

            int lastStage = Stages.Count - 1;
            int completedInShift = _plan
                .Where(js => js.StageIndex == lastStage && js.Finish <= ShiftMinutes())
                .Select(js => js.JobId)
                .Distinct()
                .Count();

            LogList?.Items.Clear();
            OutputBlock.Text = completedInShift.ToString();
            TimeBlock.Text = "t = 0.0 мин";

            RedrawStatic();
            RedrawDynamic();
        }

        private void _timer_Tick(object? sender, EventArgs e)
        {
            double speed = Math.Max(0.1, SpeedSlider.Value);
            _simTime += 0.03 * speed * 60.0 / 60.0;

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

            // если нет этапов — просто останавливаемся
            if (Stages.Count == 0) _running = false;

            TimeBlock.Text = $"t = {_simTime:F1} мин";
            RedrawDynamic();

            if (!_running) _timer.Stop();
        }

        // кнопки
        private void StartPause_Click(object sender, RoutedEventArgs e)
        {
            if (Stages.Count == 0) return; // нечего проигрывать
            if (_running) { _running = false; _timer.Stop(); }
            else { _running = true; _timer.Start(); }
        }
        private void Reset_Click(object sender, RoutedEventArgs e) => BuildPlanAndReset();
        private void AddStageBtn_Click(object sender, RoutedEventArgs e)
        {
            var stage = new Stage { Name = "Этап", MinMinutes = 10, MaxMinutes = 15, Centers = 1 };
            Stages.Add(stage);
            // выделить и прокрутить к новой строке
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

        // рисование сетки
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

            // размеры «виртуального полотна»
            double contentW = XMargin * 2 + n * StageW + (n - 1) * Gap;
            double contentH = HeadTop + lanes * (LaneH + VGap) + 10;

            canvas.Width = contentW;
            canvas.Height = contentH;

            // заголовки
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

            // полосы (каждый пост — дорожка)
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

        // рисование активных машин
        private void RedrawDynamic()
        {
            if (Stages.Count == 0) { StageCanvas.Children.Clear(); return; }

            // перерисуем сетку и наложим «машины»
            RedrawStatic();

            var canvas = StageCanvas;
            int n = Stages.Count;

            // предрасчёт Y всех полос
            var laneYs = new System.Collections.Generic.List<double>();
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
                canvas.Children.Add(car);
            }
        }
    }
}