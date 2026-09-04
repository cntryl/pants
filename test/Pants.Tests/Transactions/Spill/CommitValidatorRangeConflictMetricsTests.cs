namespace Cntryl.Pants.Transactions.Spill;

public sealed class CommitValidatorRangeConflictMetricsTests
{
    [Fact]
    public async Task ShouldClassifyOverlappingRangeTombstoneAsRangeConflictForDeleteRange()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var stale = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        stale.DeleteRange("bravo"u8.ToArray(), "yankee"u8.ToArray());
        await using (var newer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            newer.DeleteRange("alpha"u8.ToArray(), "zulu"u8.ToArray());
            await newer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            stale.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(0, metrics.WriteConflictsPointTotal);
        Assert.Equal(1, metrics.WriteConflictsRangeTotal);
    }

    [Fact]
    public async Task ShouldClassifyNewerPointMutationAsRangeConflictForDeleteRange()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var stale = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        stale.DeleteRange("bravo"u8.ToArray(), "yankee"u8.ToArray());
        await using (var newer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            newer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await newer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            stale.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(0, metrics.WriteConflictsPointTotal);
        Assert.Equal(1, metrics.WriteConflictsRangeTotal);
    }

    [Fact]
    public async Task ShouldClassifyCoveringRangeTombstoneAsPointConflictForAssertionOnlyCommit()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var stale = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stale.AssertValue("key"u8.ToArray(), null);
        await using (var newer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            newer.DeleteRange("alpha"u8.ToArray(), "zulu"u8.ToArray());
            await newer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            stale.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(1, metrics.WriteConflictsPointTotal);
        Assert.Equal(0, metrics.WriteConflictsRangeTotal);
    }

    [Fact]
    public async Task ShouldClassifyFirstAssertionConflictAsPointGivenLaterRangeAlsoConflicts()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var stale = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stale.AssertValue("asserted"u8.ToArray(), null);
        stale.DeleteRange("x"u8.ToArray(), "z"u8.ToArray());
        await using (var newer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            newer.Put("asserted"u8.ToArray(), "newer"u8.ToArray());
            newer.Put("y"u8.ToArray(), "newer"u8.ToArray());
            await newer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            stale.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(1, metrics.WriteConflictsPointTotal);
        Assert.Equal(0, metrics.WriteConflictsRangeTotal);
    }

    [Fact]
    public async Task ShouldClassifyFirstPointWriteConflictGivenLaterRangeAlsoConflicts()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var stale = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        stale.Put("point"u8.ToArray(), "stale"u8.ToArray());
        stale.DeleteRange("x"u8.ToArray(), "z"u8.ToArray());
        await using (var newer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            newer.Put("point"u8.ToArray(), "newer"u8.ToArray());
            newer.Put("y"u8.ToArray(), "newer"u8.ToArray());
            await newer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            stale.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(1, metrics.WriteConflictsPointTotal);
        Assert.Equal(0, metrics.WriteConflictsRangeTotal);
    }
}
