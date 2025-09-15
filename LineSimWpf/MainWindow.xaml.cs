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
        private double _simTime;                         // минуты «на часах»
        private bool _running;
        private System.Collections.Generic.List<JobStage> _plan = new();

        // полный лог: событие на каждом этапе = (время завершения, jobId, stageIndex)
        private System.Collections.Generic.List<(double time, int jobId, int stageIndex)> _events = new();
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

            // Таблица — явная привязка источника
            StagesGrid.ItemsSource = Stages;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += _timer_Tick;

            // Авто-пересчёт при изменениях
            Stages.CollectionChanged += Stages_CollectionChanged;
            SizeChanged += (_, __) => RedrawStatic();
            ModeBox.SelectionChanged += (_, __) => BuildPlanAndReset();
            ShiftBox.TextChanged += (_, __) => BuildPlanAndReset();
            HorizonBox.TextChanged += (_, __) => BuildPlanAndReset();

            BuildPlanAndReset(); // старт: пустая схема и нулевые метрики
        }

        // ——— вспомогательные ———
        private double ShiftMinutes() =>
            double.TryParse(ShiftBox.Text, out var v) ? v : 720;

        // число смен (целое, из HorizonBox) и горизонт в минутах
        private int NumShifts()
        {
            if (int.TryParse(HorizonBox.Text, out var k) && k > 0) return k;
            // запасной вариант: если ввели дробь — округлим вниз
            if (double.TryParse(HorizonBox.Text, out var kd) && kd > 0) return Math.Max(1, (int)Math.Floor(kd));
            return 2;
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

        // ——— события коллекции/этапов ———
        private void Stages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (Stage s in e.NewItems) s.PropertyChanged += Stage_PropertyChanged;
            if (e.OldItems != null)
                foreach (Stage s in e.OldItems) s.PropertyChanged -= Stage_PropertyChanged;

            BuildPlanAndReset();
        }

        private void Stage_PropertyChanged(object? sender, PropertyChangedEventArgs e) => BuildPlanAndReset();

        // ——— пересборка плана и сброс симуляции/лога ———
        private void BuildPlanAndReset()
        {
            if (Stages.Count == 0)
            {
                _plan.Clear();
                _events.Clear();
                _evtPtr = 0;
                _simTime = 0;
                OutputBlock.Text = "0";
                TimeBlock.Text = "t = 0.0 мин";
                PerShiftList?.Items.Clear();
                LogList?.Items.Clear();
                RedrawStatic();
                return;
            }

            var (plan, _) = Scheduler.BuildSchedule(
                Stages.ToList(),
                ShiftMinutes(),
                CurrentMode(),
                maxJobs: 200000,
                planHorizonMinutes: HorizonMinutes(),   // моделируем k смен
                continuousFeed: true                    // подача без отсечки
            );

            _plan = plan;
            _simTime = 0;
            _evtPtr = 0;

            _events = _plan
                .Select(js => (time: js.Finish, jobId: js.JobId, stageIndex: js.StageIndex))
                .OrderBy(e => e.time)
                .ToList();

            // Готово за 1-ю смену (по последнему этапу) — финиши ≤ Shift
            int lastStage = Stages.Count - 1;
            int completedShift1 = _plan
                .Where(js => js.StageIndex == lastStage && js.Finish <= ShiftMinutes())
                .Select(js => js.JobId)
                .Distinct()
                .Count();
            OutputBlock.Text = completedShift1.ToString();
            TimeBlock.Text = "t = 0.0 мин";

            // Готово по каждой смене (1..NumShifts)
            UpdatePerShiftSummary(lastStage);

            LogList?.Items.Clear();
            RedrawStatic();
            RedrawDynamic();
        }

        // расчёт «Готово по сменам»
        private void UpdatePerShiftSummary(int lastStage)
        {
            PerShiftList?.Items.Clear();

            double shift = ShiftMinutes();
            int n = NumShifts();
            if (n <= 0) n = 1;

            // все финиш-времена на последнем этапе
            var finishes = _plan
                .Where(js => js.StageIndex == lastStage)
                .Select(js => js.Finish)
                .OrderBy(t => t)
                .ToList();

            for (int i = 0; i < n; i++)
            {
                double start = i * shift;
                double end = (i + 1) * shift;

                // считаем финиши в интервале (start, end] — чтобы не было двойного учёта границ
                int count = finishes.Count(t => t > start && t <= end);
                PerShiftList.Items.Add($"Смена {i + 1}: {count}");
            }
        }

        // ——— таймер ———
        private void _timer_Tick(object? sender, EventArgs e)
        {
            double speed = Math.Max(0.1, SpeedSlider.Value); // до 100×
            _simTime += 0.03 * speed * 60.0 / 60.0;          // минуты

            // выводим события, время которых наступило
            while (_evtPtr < _events.Count && _events[_evtPtr].time <= _simTime)
            {
                var (time, jobId, stageIndex) = _events[_evtPtr];
                string stageName = (stageIndex >= 0 && stageIndex < Stages.Count)
                    ? Stages[stageIndex].Name
                    : $"Этап {stageIndex + 1}";

                LogList.Items.Insert(0, $"t={time:F1} мин: Машина #{jobId + 1} прошла {stageName}");
                _evtPtr++;

                if (LogList.Items.Count > 400)
                    LogList.Items.RemoveAt(LogList.Items.Count - 1);
            }

            if (Stages.Count == 0) _running = false;

            TimeBlock.Text = $"t = {_simTime:F1} мин";
            RedrawDynamic();

            // бежим до конца выбранного горизонта
            if (_simTime > HorizonMinutes() + 1) _running = false;
            if (!_running) _timer.Stop();
        }

        // ——— кнопки ———
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

        // ——— рисование статической схемы ———
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

            // заголовки этапов
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

        // ——— рисование активных «машин» ———
        private void RedrawDynamic()
        {
            if (Stages.Count == 0) { StageCanvas.Children.Clear(); return; }

            // перерисуем сетку и наложим «машины»
            RedrawStatic();

            // предрасчёт Y каждой полосы
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

            // активные операции
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