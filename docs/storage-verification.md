# Storage Verification Outcomes

Midge ships a `midge verify [--json] <db-path>` command whose exit codes form an operator
contract. Pants has no CLI. It exposes the same contract as a library API, so a host that wants a
verify command can build one on top of it.

## API

```csharp
var outcome = await PantsDatabase.VerifyPathOutcomeAsync(path, cancellationToken);
return outcome.ExitCode;
```

`PantsDatabase.VerifyPathOutcomeAsync` runs the same offline verification as
`PantsDatabase.VerifyPathAsync`. Instead of throwing, it returns a
`PantsStorageVerificationOutcome`:

| Property      | Meaning                                                                     |
|---------------|-----------------------------------------------------------------------------|
| `Kind`        | The `PantsStorageVerificationOutcomeKind` outcome class.                    |
| `ExitCode`    | `(int)Kind`, which matches the `midge verify` exit code.                    |
| `Report`      | The `PantsStorageVerificationReport` when verification completed, otherwise `null`. |
| `ErrorCode`   | The `PantsErrorCode` of the failure, otherwise `null`.                      |
| `PathFailure` | A `PantsStoragePathFailure` when the path failed pre-validation, otherwise `null`. |
| `Message`     | A human-readable failure description, otherwise `null`.                     |

Only cancellation throws. `VerifyPathAsync` keeps its existing behavior and still throws.

Online verification (`IPantsPersistentStorage.VerifyAsync`) is classified with the same rules:

- `PantsStorageVerificationOutcome.FromReport(report)` for a completed report.
- `PantsStorageVerificationOutcome.FromException(exception)` for a `PantsException`.

The underlying rules are also available as extension methods. Both overloads are named
`GetVerificationOutcomeKind`, one on `PantsEngineHealth` and one on `PantsErrorCode`.

## Outcome classes

| Kind         | Exit code | Produced by                                                                   |
|--------------|-----------|-------------------------------------------------------------------------------|
| `Healthy`    | 0         | Report health `Healthy`.                                                       |
| `Degraded`   | 1         | Report health `Degraded`, `SalvageMode` or `WriteStalled`; `Backpressure` severity errors (`Busy`, `Timeout`, `WriteStall`, `NoSpace`). |
| `Usage`      | 2         | `Caller` severity errors other than `NotFound` and `InvalidPath`.              |
| `Storage`    | 3         | Path pre-validation failures; `NotFound`, `InvalidPath`; `Transient` and `Fenced` severity errors. |
| `Corruption` | 4         | Report health `Corrupt`; `Fatal` severity errors (`Corruption`, `RecoveryFailed`, `CompatibilityError`). |
| `Internal`   | 5         | `Defect` severity errors (`Internal`, `ResourceLimit`), and unexpected exceptions. |

Error classification starts from `PantsErrorCode.GetSeverity()`. A new error code therefore needs
to be classified in one place only. Backpressure is reported as degraded, not internal, because it
is a condition to retry, not a defect to report.

## Path pre-validation

Before reading storage contents, the path is checked the way Midge checks it. Each failure is
a `Storage` outcome:

| `PathFailure`   | `ErrorCode`   | Condition                                                                        |
|-----------------|---------------|----------------------------------------------------------------------------------|
| `Invalid`       | `InvalidPath` | The path is empty, whitespace, or malformed for the platform.                    |
| `NotADirectory` | `InvalidPath` | The path names a file.                                                           |
| `Missing`       | `NotFound`    | The path does not exist.                                                         |
| `Inaccessible`  | `Io`          | The directory cannot be listed, or `FORMAT`, the manifest, `manifest.journal` or `intent_log.json` exists but cannot be opened for reading. |

Storage files that are absent are not path failures. Verification itself reports them, usually as
`Corruption`.

## JSON

Pants does not define a JSON schema for verification outcomes. The Midge CLI's `--json` envelope
uses `schema_version` 1 and `verification_scope` `local_path`. A host tool that needs that envelope
can serialize `Report` (snake_case) and `Kind` itself.
