# Fault Injection

Pants exposes deterministic persistence failpoints to its test assembly through
`PantsRuntimeDependencies`. They are internal implementation seams, not public
configuration and not enabled in production opens.

Implement `IPantsFailpointHandler.Hit(PantsFailpoint)` and open the database with
`PantsDatabase.OpenForTestingAsync`. A handler should fail a named boundary once
and then allow recovery or retry to proceed. Keep handlers synchronous and avoid
wall-clock timing so Release test runs remain deterministic.

Covered boundaries include partial WAL append and rotation, SST durability, flush and
compaction publication, intent-log replacement, manifest journal append/sync,
manifest checkpoints, cloud upload acknowledgement, and lease renewal.

Tests must assert the externally visible recovery invariant, not merely that an
exception was thrown. For example, a failed atomic transaction must reopen as
fully present or fully absent; a durable but unpublished SST may remain as
conservative residue and must be safely reusable on retry.

Use `TemporaryDirectory` for every persistent scenario and reopen through a new
database instance after the injected failure. Never delete uncertain files in a
test handler: Pants intentionally prefers a recoverable storage leak over unsafe
deletion.
