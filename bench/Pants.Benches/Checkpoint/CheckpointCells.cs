using System.Collections.Immutable;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointCells
{
    public const int ValueBytes = 1024;
    public static readonly TimeSpan CellBudget = TimeSpan.FromMinutes(15);
    public static readonly ImmutableArray<Cell> Workloads =
    [
        new("A", "checkpoint_local_256x1mib_1cf", 256, 1024, 1),
        new("B", "checkpoint_local_512x256kib_16cf", 512, 256, 16),
        new("C", "checkpoint_local_1024x64kib_1cf", 1024, 64, 1)
    ];
    public static readonly ImmutableArray<Selection> Attempts =
        [.. Workloads.SelectMany(static cell => Enumerable.Range(1, 3).Select(repeat => new Selection(cell, repeat)))];

    public static Selection Select(string workload, int repeat) =>
        Attempts.SingleOrDefault(selection => selection.Cell.Workload == workload && selection.Repeat == repeat)
        ?? throw new ArgumentException("Select exactly one registered workload and repeat 1, 2 or 3.");

    public sealed record Cell(string Id, string Workload, int Cycles, int Rows, int Families)
    {
        public int WarmupCycles => (Cycles + 9) / 10;
        public int MeasuredCycles => Cycles - WarmupCycles;
    }

    public sealed record Selection(Cell Cell, int Repeat)
    {
        public string Key => $"{Cell.Id}-r{Repeat}";
    }
}
