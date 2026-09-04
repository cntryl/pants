using System.Security.Cryptography;
using System.Text.Json;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class FixtureRefreshPublisher
{
    const int TransactionSchemaVersion = 1;
    const string PreparedState = "prepared";
    const string CommittedState = "committed";

    static readonly JsonSerializerOptions TransactionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Recover(PantsRepositoryPaths repository, bool forceRefresh)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var marker = TransactionMarker(repository);
        var markerTemporary = TransactionMarkerTemporary(repository);
        var fixturesBackup = FixturesBackup(repository);
        var manifestBackup = ManifestBackup(repository);
        var fixturesCleanup = FixturesCleanup(repository);
        var manifestCleanup = ManifestCleanup(repository);
        var fixturesDiscard = FixturesDiscard(repository);
        if (!File.Exists(marker))
        {
            if (Directory.Exists(fixturesBackup)
                || File.Exists(manifestBackup)
                || Directory.Exists(fixturesCleanup)
                || File.Exists(manifestCleanup)
                || Directory.Exists(fixturesDiscard))
            {
                throw new InvalidOperationException(
                    "Compatibility refresh backup or cleanup paths exist without a transaction "
                    + "marker. No files were changed; inspect them before continuing.");
            }

            File.Delete(markerTemporary);
            return;
        }

        var transaction = ReadTransaction(marker);
        switch (transaction.State)
        {
            case PreparedState:
                RestorePrepared(
                    repository,
                    transaction,
                    fixturesBackup,
                    manifestBackup,
                    fixturesDiscard,
                    forceRefresh);
                DeleteTransactionMarkers(marker, markerTemporary);
                Console.Error.WriteLine(
                    "warning: rolled back an interrupted compatibility refresh transaction.");
                break;
            case CommittedState:
                RecoverCommitted(
                    repository,
                    transaction,
                    fixturesBackup,
                    manifestBackup,
                    fixturesCleanup,
                    manifestCleanup,
                    fixturesDiscard,
                    forceRefresh);
                DeleteTransactionMarkers(marker, markerTemporary);
                Console.Error.WriteLine(
                    "warning: completed cleanup for an interrupted compatibility refresh.");
                break;
            default:
                throw new InvalidDataException(
                    $"Compatibility refresh transaction marker has unknown state "
                    + $"'{transaction.State}'.");
        }
    }

    public static void Publish(
        PantsRepositoryPaths repository,
        FixtureRefreshWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workspace);

        var marker = TransactionMarker(repository);
        var markerTemporary = TransactionMarkerTemporary(repository);
        var fixturesBackup = FixturesBackup(repository);
        var manifestBackup = ManifestBackup(repository);
        var fixturesCleanup = FixturesCleanup(repository);
        var manifestCleanup = ManifestCleanup(repository);
        var fixturesDiscard = FixturesDiscard(repository);
        EnsureTransactionPathsAreClear(
            marker,
            markerTemporary,
            fixturesBackup,
            manifestBackup,
            fixturesCleanup,
            manifestCleanup,
            fixturesDiscard);
        var transaction = CreateTransaction(repository, workspace);
        WriteTransaction(marker, markerTemporary, transaction, overwrite: false);

        try
        {
            Directory.Move(repository.CompatibilityFixtures, fixturesBackup);
            File.Move(repository.ContractManifest, manifestBackup);
            Directory.Move(workspace.CompatibilityFixtures, repository.CompatibilityFixtures);
            File.Move(workspace.ContractManifest, repository.ContractManifest);
            WriteTransaction(
                marker,
                markerTemporary,
                transaction with { State = CommittedState },
                overwrite: true);
        }
        catch (Exception publishException)
        {
            try
            {
                RestorePrepared(
                    repository,
                    transaction,
                    fixturesBackup,
                    manifestBackup,
                    fixturesDiscard,
                    forceRefresh: false);
                DeleteTransactionMarkers(marker, markerTemporary);
            }
            catch (Exception restoreException)
            {
                throw new AggregateException(
                    "Compatibility fixture publication failed and could not be rolled back.",
                    publishException,
                    restoreException);
            }

            throw;
        }

        if (TryCleanupBackups(
                transaction,
                fixturesBackup,
                manifestBackup,
                fixturesCleanup,
                manifestCleanup))
        {
            DeleteTransactionMarkers(marker, markerTemporary);
        }
    }

    static FixtureRefreshTransaction CreateTransaction(
        PantsRepositoryPaths repository,
        FixtureRefreshWorkspace workspace) =>
        new(
            TransactionSchemaVersion,
            PreparedState,
            DirectoryTreeFingerprint.Compute(repository.CompatibilityFixtures),
            ComputeFileSha256(repository.ContractManifest),
            DirectoryTreeFingerprint.Compute(workspace.CompatibilityFixtures),
            ComputeFileSha256(workspace.ContractManifest));

    static FixtureRefreshTransaction ReadTransaction(string marker)
    {
        var transaction = JsonSerializer.Deserialize<FixtureRefreshTransaction>(
                File.ReadAllBytes(marker),
                TransactionJsonOptions)
            ?? throw new InvalidDataException(
                "Compatibility refresh transaction marker is empty.");
        if (transaction.SchemaVersion != TransactionSchemaVersion)
        {
            throw new InvalidDataException(
                $"Compatibility refresh transaction schema '{transaction.SchemaVersion}' "
                + "is unsupported.");
        }

        return transaction;
    }

    static void RestorePrepared(
        PantsRepositoryPaths repository,
        FixtureRefreshTransaction transaction,
        string fixturesBackup,
        string manifestBackup,
        string fixturesDiscard,
        bool forceRefresh)
    {
        ValidatePreparedDirectory(
            repository.CompatibilityFixtures,
            fixturesBackup,
            fixturesDiscard,
            transaction.PreviousFixturesSha256,
            transaction.NextFixturesSha256,
            forceRefresh);
        ValidatePreparedFile(
            repository.ContractManifest,
            manifestBackup,
            transaction.PreviousManifestSha256,
            transaction.NextManifestSha256,
            forceRefresh);
        RestorePreparedDirectory(
            repository.CompatibilityFixtures,
            fixturesBackup,
            fixturesDiscard,
            transaction.PreviousFixturesSha256,
            transaction.NextFixturesSha256,
            forceRefresh);
        RestorePreparedFile(
            repository.ContractManifest,
            manifestBackup,
            transaction.PreviousManifestSha256,
            transaction.NextManifestSha256,
            forceRefresh);
    }

    static void ValidatePreparedDirectory(
        string livePath,
        string backupPath,
        string discardPath,
        string previousSha256,
        string nextSha256,
        bool forceRefresh)
    {
        if (!Directory.Exists(backupPath))
        {
            EnsureUnmovedDirectoryIsSafe(livePath, previousSha256, forceRefresh);
            return;
        }

        EnsureDirectoryHash(backupPath, previousSha256, "fixture backup");
        if (Directory.Exists(discardPath))
        {
            if (Directory.Exists(livePath))
            {
                throw ConflictingCleanupPaths(livePath, discardPath);
            }

            return;
        }

        if (Directory.Exists(livePath))
        {
            EnsureKnownRecoveryTarget(
                livePath,
                DirectoryTreeFingerprint.Compute(livePath),
                previousSha256,
                nextSha256,
                forceRefresh);
        }
    }

    static void ValidatePreparedFile(
        string livePath,
        string backupPath,
        string previousSha256,
        string nextSha256,
        bool forceRefresh)
    {
        if (!File.Exists(backupPath))
        {
            EnsureUnmovedFileIsSafe(livePath, previousSha256, forceRefresh);
            return;
        }

        EnsureFileHash(backupPath, previousSha256, "manifest backup");
        if (File.Exists(livePath))
        {
            EnsureKnownRecoveryTarget(
                livePath,
                ComputeFileSha256(livePath),
                previousSha256,
                nextSha256,
                forceRefresh);
        }
    }

    static void RestorePreparedDirectory(
        string livePath,
        string backupPath,
        string discardPath,
        string previousSha256,
        string nextSha256,
        bool forceRefresh)
    {
        if (!Directory.Exists(backupPath))
        {
            EnsureUnmovedDirectoryIsSafe(livePath, previousSha256, forceRefresh);
            DeletePreparedDiscard(discardPath);
            return;
        }

        EnsureDirectoryHash(backupPath, previousSha256, "fixture backup");
        if (Directory.Exists(discardPath))
        {
            if (Directory.Exists(livePath))
            {
                throw ConflictingCleanupPaths(livePath, discardPath);
            }

            Directory.Move(backupPath, livePath);
            DeletePreparedDiscard(discardPath);
            return;
        }

        if (Directory.Exists(livePath))
        {
            var liveHash = DirectoryTreeFingerprint.Compute(livePath);
            EnsureKnownRecoveryTarget(
                livePath,
                liveHash,
                previousSha256,
                nextSha256,
                forceRefresh);
            Directory.Move(livePath, discardPath);
        }

        Directory.Move(backupPath, livePath);
        DeletePreparedDiscard(discardPath);
    }

    static void RestorePreparedFile(
        string livePath,
        string backupPath,
        string previousSha256,
        string nextSha256,
        bool forceRefresh)
    {
        if (!File.Exists(backupPath))
        {
            EnsureUnmovedFileIsSafe(livePath, previousSha256, forceRefresh);
            return;
        }

        EnsureFileHash(backupPath, previousSha256, "manifest backup");
        if (File.Exists(livePath))
        {
            var liveHash = ComputeFileSha256(livePath);
            EnsureKnownRecoveryTarget(
                livePath,
                liveHash,
                previousSha256,
                nextSha256,
                forceRefresh);
            File.Delete(livePath);
        }

        File.Move(backupPath, livePath);
    }

    static void EnsureUnmovedDirectoryIsSafe(
        string livePath,
        string previousSha256,
        bool forceRefresh)
    {
        if (!Directory.Exists(livePath))
        {
            throw new InvalidDataException(
                $"Prepared compatibility refresh has neither live nor backup fixtures at "
                + $"'{livePath}'.");
        }

        var actual = DirectoryTreeFingerprint.Compute(livePath);
        if (!StringComparer.Ordinal.Equals(actual, previousSha256) && !forceRefresh)
        {
            throw AmbiguousRecovery(livePath, actual);
        }
    }

    static void EnsureUnmovedFileIsSafe(
        string livePath,
        string previousSha256,
        bool forceRefresh)
    {
        if (!File.Exists(livePath))
        {
            throw new InvalidDataException(
                $"Prepared compatibility refresh has neither live nor backup manifest at "
                + $"'{livePath}'.");
        }

        var actual = ComputeFileSha256(livePath);
        if (!StringComparer.Ordinal.Equals(actual, previousSha256) && !forceRefresh)
        {
            throw AmbiguousRecovery(livePath, actual);
        }
    }

    static void EnsureKnownRecoveryTarget(
        string path,
        string actualSha256,
        string previousSha256,
        string nextSha256,
        bool forceRefresh)
    {
        var known = StringComparer.Ordinal.Equals(actualSha256, previousSha256)
            || StringComparer.Ordinal.Equals(actualSha256, nextSha256);
        if (!known && !forceRefresh)
        {
            throw AmbiguousRecovery(path, actualSha256);
        }
    }

    static void RecoverCommitted(
        PantsRepositoryPaths repository,
        FixtureRefreshTransaction transaction,
        string fixturesBackup,
        string manifestBackup,
        string fixturesCleanup,
        string manifestCleanup,
        string fixturesDiscard,
        bool forceRefresh)
    {
        if (!Directory.Exists(repository.CompatibilityFixtures)
            || !File.Exists(repository.ContractManifest))
        {
            throw new InvalidOperationException(
                "A committed compatibility refresh is missing a published target. "
                + "No recovery was attempted.");
        }

        if (Directory.Exists(fixturesDiscard))
        {
            throw new InvalidOperationException(
                $"Committed compatibility refresh has an unexpected prepared discard path "
                + $"'{fixturesDiscard}'. No files were changed.");
        }

        var fixturesHash = DirectoryTreeFingerprint.Compute(repository.CompatibilityFixtures);
        var manifestHash = ComputeFileSha256(repository.ContractManifest);
        if ((!StringComparer.Ordinal.Equals(fixturesHash, transaction.NextFixturesSha256)
                || !StringComparer.Ordinal.Equals(manifestHash, transaction.NextManifestSha256))
            && !forceRefresh)
        {
            throw new InvalidOperationException(
                "Committed compatibility refresh targets changed after publication. No files "
                + "were deleted; inspect them or pass '--force' to start a fresh refresh.");
        }

        if (Directory.Exists(fixturesBackup) && Directory.Exists(fixturesCleanup))
        {
            throw ConflictingCleanupPaths(fixturesBackup, fixturesCleanup);
        }

        if (File.Exists(manifestBackup) && File.Exists(manifestCleanup))
        {
            throw ConflictingCleanupPaths(manifestBackup, manifestCleanup);
        }

        if (!TryCleanupBackups(
                transaction,
                fixturesBackup,
                manifestBackup,
                fixturesCleanup,
                manifestCleanup))
        {
            throw new InvalidOperationException(
                "A committed compatibility refresh could not finish backup cleanup.");
        }
    }

    static bool TryCleanupBackups(
        FixtureRefreshTransaction transaction,
        string fixturesBackup,
        string manifestBackup,
        string fixturesCleanup,
        string manifestCleanup)
    {
        if (Directory.Exists(fixturesBackup))
        {
            EnsureDirectoryHash(
                fixturesBackup,
                transaction.PreviousFixturesSha256,
                "fixture backup");
            try
            {
                Directory.Move(fixturesBackup, fixturesCleanup);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"warning: could not stage fixture backup '{fixturesBackup}' for cleanup: "
                    + exception.Message);
            }
        }

        if (File.Exists(manifestBackup))
        {
            EnsureFileHash(
                manifestBackup,
                transaction.PreviousManifestSha256,
                "manifest backup");
            try
            {
                File.Move(manifestBackup, manifestCleanup);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"warning: could not stage contract manifest backup '{manifestBackup}' "
                    + "for cleanup: " + exception.Message);
            }
        }

        return TryDeleteCleanup(fixturesBackup, manifestBackup, fixturesCleanup, manifestCleanup);
    }

    static void EnsureDirectoryHash(string path, string expectedSha256, string description)
    {
        var actual = DirectoryTreeFingerprint.Compute(path);
        if (!StringComparer.Ordinal.Equals(actual, expectedSha256))
        {
            throw new InvalidDataException(
                $"Compatibility {description} '{path}' has hash '{actual}', expected "
                + $"'{expectedSha256}'. No files were deleted.");
        }
    }

    static void EnsureFileHash(string path, string expectedSha256, string description)
    {
        var actual = ComputeFileSha256(path);
        if (!StringComparer.Ordinal.Equals(actual, expectedSha256))
        {
            throw new InvalidDataException(
                $"Compatibility {description} '{path}' has hash '{actual}', expected "
                + $"'{expectedSha256}'. No files were deleted.");
        }
    }

    static InvalidOperationException AmbiguousRecovery(string path, string actualSha256) =>
        new(
            $"Interrupted compatibility refresh target '{path}' has unrecognized hash "
            + $"'{actualSha256}'. No files were deleted; inspect it or pass '--force' to "
            + "replace post-interruption edits.");

    static void EnsureTransactionPathsAreClear(
        string marker,
        string markerTemporary,
        string fixturesBackup,
        string manifestBackup,
        string fixturesCleanup,
        string manifestCleanup,
        string fixturesDiscard)
    {
        if (File.Exists(marker)
            || File.Exists(markerTemporary)
            || Directory.Exists(fixturesBackup)
            || File.Exists(manifestBackup)
            || Directory.Exists(fixturesCleanup)
            || File.Exists(manifestCleanup)
            || Directory.Exists(fixturesDiscard))
        {
            throw new InvalidOperationException(
                "Compatibility refresh transaction paths are not clear. Run recovery before "
                + "publishing another refresh.");
        }
    }

    static void WriteTransaction(
        string marker,
        string markerTemporary,
        FixtureRefreshTransaction transaction,
        bool overwrite)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            transaction,
            TransactionJsonOptions);
        using (var stream = new FileStream(
                   markerTemporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(serialized);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }

        File.Move(markerTemporary, marker, overwrite);
    }

    static bool TryDeleteCleanup(
        string fixturesBackup,
        string manifestBackup,
        string fixturesCleanup,
        string manifestCleanup)
    {
        try
        {
            if (Directory.Exists(fixturesCleanup))
            {
                Directory.Delete(fixturesCleanup, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: could not remove staged fixture backup '{fixturesCleanup}': "
                + exception.Message);
        }

        try
        {
            File.Delete(manifestCleanup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: could not remove staged contract manifest backup "
                + $"'{manifestCleanup}': "
                + exception.Message);
        }

        return !Directory.Exists(fixturesBackup)
            && !File.Exists(manifestBackup)
            && !Directory.Exists(fixturesCleanup)
            && !File.Exists(manifestCleanup);
    }

    static InvalidOperationException ConflictingCleanupPaths(string backup, string cleanup) =>
        new(
            $"Compatibility refresh backup '{backup}' and cleanup path '{cleanup}' both exist. "
            + "No files were changed; inspect them before continuing.");

    static void DeletePreparedDiscard(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Prepared compatibility refresh restored the previous fixture tree but could "
                + $"not finish discard cleanup at '{path}'.",
                exception);
        }
    }

    static void DeleteTransactionMarkers(string marker, string markerTemporary)
    {
        File.Delete(markerTemporary);
        File.Delete(marker);
    }

    static string ComputeFileSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    static string FixturesBackup(PantsRepositoryPaths repository) =>
        $"{repository.CompatibilityFixtures}.refresh-backup";

    static string ManifestBackup(PantsRepositoryPaths repository) =>
        $"{repository.ContractManifest}.refresh-backup";

    static string FixturesCleanup(PantsRepositoryPaths repository) =>
        $"{repository.CompatibilityFixtures}.refresh-cleanup";

    static string ManifestCleanup(PantsRepositoryPaths repository) =>
        $"{repository.ContractManifest}.refresh-cleanup";

    static string FixturesDiscard(PantsRepositoryPaths repository) =>
        $"{repository.CompatibilityFixtures}.refresh-discard";

    static string TransactionMarker(PantsRepositoryPaths repository) =>
        $"{repository.ContractManifest}.refresh-transaction";

    static string TransactionMarkerTemporary(PantsRepositoryPaths repository) =>
        $"{TransactionMarker(repository)}.tmp";
}
