namespace Cntryl.Pants.Tests.Storage;

/// <summary>
/// Slice 6b (issue #219): a shared bounded-resource accounting guard for streaming pipelines
/// (compaction merge buffers, scan k-way merge buffers), mirroring Midge's
/// <c>ResourceBudget</c>/<c>ResourceReservation</c>.
/// </summary>
public sealed class ResourceBudgetTests
{
    [Fact]
    public void ShouldReserveUnderTheLimitAndTrackCurrentAndPeak()
    {
        var budget = new ResourceBudget(100);

        using (budget.Reserve(40))
        {
            Assert.Equal(40, budget.Current);
            Assert.Equal(40, budget.Peak);

            using (budget.Reserve(30))
            {
                Assert.Equal(70, budget.Current);
                Assert.Equal(70, budget.Peak);
            }

            Assert.Equal(40, budget.Current);
            Assert.Equal(70, budget.Peak);
        }

        Assert.Equal(0, budget.Current);
        Assert.Equal(70, budget.Peak);
    }

    [Fact]
    public void ShouldRejectAReservationThatWouldExceedTheLimit()
    {
        var budget = new ResourceBudget(100);
        using var held = budget.Reserve(80);

        var exception = Assert.Throws<PantsResourceLimitException>(() => budget.Reserve(21));

        Assert.Equal(80, budget.Current);
        _ = exception;
    }

    [Fact]
    public void ShouldRejectOverflowWithoutCorruptingCurrentUsage()
    {
        var budget = new ResourceBudget(long.MaxValue);
        using var held = budget.Reserve(long.MaxValue);

        Assert.Throws<PantsResourceLimitException>(() => budget.Reserve(1));
        Assert.Equal(long.MaxValue, budget.Current);
    }

    [Fact]
    public void ShouldAllowAReservationExactlyAtTheRemainingLimit()
    {
        var budget = new ResourceBudget(100);
        using var held = budget.Reserve(60);

        using var second = budget.Reserve(40);

        Assert.Equal(100, budget.Current);
    }

    [Fact]
    public void ShouldBeIdempotentOnDoubleDispose()
    {
        var budget = new ResourceBudget(100);
        var reservation = budget.Reserve(10);

        reservation.Dispose();
        reservation.Dispose();

        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public void ShouldRejectNegativeReservations()
    {
        var budget = new ResourceBudget(100);

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Reserve(-1));
    }

    [Fact]
    public async Task ShouldAccountConcurrentReservationsAtomically()
    {
        var budget = new ResourceBudget(1_000_000);
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                using var reservation = budget.Reserve(1);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(0, budget.Current);
        Assert.True(budget.Peak > 0);
    }
}
