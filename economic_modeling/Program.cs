using System;
using System.Collections.Generic;

enum TimeMode { Min, Max, Random }

record Stage(string Name, double MinMinutes, double MaxMinutes, int Centers);

class FlowSimulator
{
    private readonly List<Stage> _stages;
    private readonly TimeMode _mode;
    private readonly Random _rng;

    public FlowSimulator(List<Stage> stages, TimeMode mode = TimeMode.Min, int seed = 42)
    {
        _stages = stages;
        _mode = mode;
        _rng = new Random(seed);
    }

    private double DrawTime(Stage s) =>
        _mode switch
        {
            TimeMode.Min => s.MinMinutes,
            TimeMode.Max => s.MaxMinutes,
            TimeMode.Random => s.MinMinutes == s.MaxMinutes
                ? s.MinMinutes
                : _rng.Next((int)Math.Round(s.MinMinutes), (int)Math.Round(s.MaxMinutes) + 1),
            _ => s.MinMinutes
        };

    /// <summary>
    /// Возвращает количество машин, завершивших последний этап к моменту ShiftMinutes.
    /// </summary>
    public int Simulate(double ShiftMinutes, int maxJobs = 10000, bool printSample = true)
    {
        // nextFree[s][i] — момент, когда i-й пост этапа s освободится
        var nextFree = new List<double[]>();
        var proc = new double[_stages.Count];
        for (int s = 0; s < _stages.Count; s++)
        {
            nextFree.Add(new double[_stages[s].Centers]);
            proc[s] = DrawTime(_stages[s]);
        }

        var finishes = new List<double>();
        int done = 0;

        for (int j = 0; j < maxJobs; j++)
        {
            double t = 0.0; // готовность текущей машины к входу на следующий этап

            for (int s = 0; s < _stages.Count; s++)
            {
                // найти самый ранний свободный пост на этапе s
                int bestIdx = 0;
                double bestTime = nextFree[s][0];
                for (int i = 1; i < nextFree[s].Length; i++)
                {
                    if (nextFree[s][i] < bestTime)
                    {
                        bestTime = nextFree[s][i];
                        bestIdx = i;
                    }
                }

                double start = Math.Max(t, bestTime);
                double finish = start + proc[s];
                nextFree[s][bestIdx] = finish;
                t = finish; // время прибытия на следующий этап
            }

            finishes.Add(t);
            if (t <= ShiftMinutes) done++;
            else break; // дальше будет только позже
        }

        if (printSample)
        {
            Console.WriteLine("Первые времена готовности (мин):");
            for (int i = 0; i < Math.Min(finishes.Count, 20); i++)
                Console.WriteLine($"{i + 1,2}: {finishes[i]}");
        }

        return done;
    }
}

class Program
{
    static void Main()
    {
        // Входные данные из твоей задачи
        var stages = new List<Stage>
        {
            new("КПП (въезд)",     20, 40, 1),
            new("Очистка цистерны",120,120,6),
            new("ОТК #1",          30, 60, 2),
            new("Загрузка",        20, 20, 1),
            new("ОТК #2",          30, 60, 2),
            new("КПП (выезд)",     10, 20, 1),
        };

        double shift = 720; // длительность смены в минутах

        // -------- режим MIN (нижние границы времени) --------
        var simMin = new FlowSimulator(stages, TimeMode.Min);
        int outMin = simMin.Simulate(shift);
        Console.WriteLine($"\nВыпуск за смену (MIN-времена): {outMin} машин");

        // -------- режим MAX (верхние границы времени) -------
        var simMax = new FlowSimulator(stages, TimeMode.Max);
        int outMax = simMax.Simulate(shift, printSample: false);
        Console.WriteLine($"Выпуск за смену (MAX-времена): {outMax} машин");

        // -------- режим RANDOM (внутри диапазонов) ----------
        var simRnd = new FlowSimulator(stages, TimeMode.Random, seed: 123);
        int outRnd = simRnd.Simulate(shift, printSample: false);
        Console.WriteLine($"Выпуск за смену (RANDOM-времена): {outRnd} машин (seed=123)");
    }
}