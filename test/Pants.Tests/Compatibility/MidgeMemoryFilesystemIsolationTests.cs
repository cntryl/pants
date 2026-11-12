using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

[Collection(CrashProcessTestGroup.Name)]
public sealed class MidgeMemoryFilesystemIsolationTests
{
    const string DirectoryVariable = "PANTS_MEMORY_FILESYSTEM_DIRECTORY";
    const string BackendVariable = "PANTS_MEMORY_FILESYSTEM_BACKEND";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldObserveFilesystemIsolationInASeparateProcess(bool persistentControl)
    {
        using var directory = new TemporaryDirectory();
        using var results = new TemporaryDirectory();
        var start = CrashChildProcess.CreateStartInfo(
            typeof(MidgeMemoryFilesystemIsolationTests),
            nameof(ShouldExerciseTheSelectedBackendInChild));
        start.WorkingDirectory = directory.Path;
        start.ArgumentList.Add($"--ResultsDirectory:{results.Path}");
        start.Environment[DirectoryVariable] = directory.Path;
        start.Environment[BackendVariable] = persistentControl ? "local" : "memory";

        using var child = await CrashChildProcess.RunToExitAsync(
            start, "memory filesystem isolation child", TestTimeouts.Expected);

        Assert.True(child.ExitCode == 0, await child.DescribeAsync());
        if (persistentControl)
        {
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(directory.Path));
            Assert.NotEmpty(Directory.EnumerateFiles(directory.Path, "*.sst", SearchOption.AllDirectories));
        }
        else
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        }
    }

    [Fact]
    public async Task ShouldExerciseTheSelectedBackendInChild()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (directory is null)
        {
            return;
        }

        // Only the isolated child changes its current directory. The parent suite can run
        // concurrently without observing a process-wide directory mutation.
        Directory.SetCurrentDirectory(directory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        var persistentControl = Environment.GetEnvironmentVariable(BackendVariable) == "local";
        var options = persistentControl
            ? PantsOpenOptions.Local(Path.Combine(directory, "database"))
            : PantsOpenOptions.InMemory();
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("isolation");
            await using (var transaction = await database.Transactions.BeginAsync(
                             family, PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 500; index++)
                {
                    transaction.Put(TestBytes.FromString($"key-{index:0000}"), "value"u8.ToArray());
                }

                await transaction.CommitAsync(persistentControl ? PantsWriteOptions.Sync : PantsWriteOptions.Buffered);
            }

            await using (var reader = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly))
            {
                for (var index = 0; index < 500; index++)
                {
                    Assert.Equal("value", TestBytes.ToText(
                        (await reader.GetAsync(TestBytes.FromString($"key-{index:0000}")))!.Value));
                }
            }

            if (persistentControl)
            {
                await database.Maintenance.FlushAsync(family);
            }
            else
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var families = await reopened.ColumnFamilies.ListAsync();
        Assert.Equal(persistentControl, families.Any(family => family.Name == "isolation"));
        if (!persistentControl)
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
    }
}
