using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace LineSimWpf
{
    public enum TimeMode { Min, Max, RandomEachJob }

    public class Stage : INotifyPropertyChanged
    {
        private string _name = "Этап";
        private double _min = 10;
        private double _max = 10;
        private int _centers = 1;

        public string Name { get => _name; set { _name = value; OnChanged(nameof(Name)); } }
        public double MinMinutes { get => _min; set { _min = value; OnChanged(nameof(MinMinutes)); } }
        public double MaxMinutes { get => _max; set { _max = value; OnChanged(nameof(MaxMinutes)); } }
        public int Centers { get => _centers; set { _centers = Math.Max(1, value); OnChanged(nameof(Centers)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public record JobStage(int JobId, int StageIndex, int CenterIndex, double Start, double Finish);

    public static class Scheduler
    {
        public static (List<JobStage> plan, int completed) BuildSchedule(
            IList<Stage> stages, double shiftMinutes, TimeMode mode, int maxJobs = 1000, int? seed = 12345)
        {
            var rng = seed.HasValue ? new Random(seed.Value) : new Random();

            var nextFree = stages.Select(s => new double[s.Centers]).ToArray();
            var plan = new List<JobStage>();
            int done = 0;

            for (int j = 0; j < maxJobs; j++)
            {
                double t = 0.0;
                for (int s = 0; s < stages.Count; s++)
                {
                    var st = stages[s];
                    double dur = mode switch
                    {
                        TimeMode.Min => st.MinMinutes,
                        TimeMode.Max => st.MaxMinutes,
                        TimeMode.RandomEachJob => st.MinMinutes == st.MaxMinutes
                            ? st.MinMinutes
                            : rng.Next((int)Math.Round(st.MinMinutes), (int)Math.Round(st.MaxMinutes) + 1),
                        _ => st.MinMinutes
                    };

                    // ищем свободный пост
                    int bestIdx = 0;
                    double best = nextFree[s][0];
                    for (int i = 1; i < nextFree[s].Length; i++)
                        if (nextFree[s][i] < best) { best = nextFree[s][i]; bestIdx = i; }

                    double start = Math.Max(t, best);
                    double finish = start + dur;
                    nextFree[s][bestIdx] = finish;
                    t = finish;

                    plan.Add(new JobStage(j, s, bestIdx, start, finish));
                }

                if (t <= shiftMinutes) done++;
                else break;
            }

            return (plan, done);
        }
    }
}