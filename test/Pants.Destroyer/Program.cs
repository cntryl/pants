using Cntryl.Pants;
using Cntryl.Pants.Transactions;

// Scenario tests spawn this same assembly's own apphost as a worker
// subprocess (see RecoveryCrashLoopTests) so they can kill a real, separate
// OS process mid-write — the only way to reproduce true crash behavior
// (partial writes, fsync ordering) rather than simulating one in-process.
// `dotnet test` never reaches this Main; VSTest hosts the test classes
// directly, so there is no conflict between "test project" and "worker".
//
// Usage: Cntryl.Pants.Destroyer <db-path> <operation-count> <seed>

if (args.Length < 3)
{
    await Console.Error.WriteLineAsync(
        "usage: Cntryl.Pants.Destroyer <db-path> <operation-count> <seed>");
    return 2;
}

var dbPath = args[0];
var operationCount = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var seed = ulong.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);

await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(dbPath));

for (var sequence = 0; sequence < operationCount; sequence++)
{
    var key = $"destroyer-key-{seed}-{sequence}";
    var value = $"destroyer-value-{seed}-{sequence}";

    await using var writer = await database.BeginTransactionAsync(
        database.DefaultColumnFamily,
        PantsTransactionMode.ReadWrite);
    writer.Put(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(value));
    await writer.CommitAsync(PantsWriteOptions.Sync);

    Console.WriteLine($"{{\"sequence\":{sequence},\"key\":{System.Text.Json.JsonSerializer.Serialize(key)},\"status\":\"acked\"}}");
    Console.Out.Flush();
}

return 0;
