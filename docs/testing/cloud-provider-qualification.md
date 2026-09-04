# Cloud provider qualification

Sqrzl is the only cloud qualification environment for Pants. The Compose service uses the same
pinned emulator image as Midge and provides S3, Azure Blob, GCS XML, and GCS JSON front doors
without live-cloud credentials. Repository and CI qualification is Sqrzl-only and never accesses
live provider accounts.

OCI configuration and request routing have deterministic structural and provider-shaped coverage,
but Sqrzl does not expose an OCI identity. No live OCI qualification is claimed or run by Pants.

Start the emulator:

```console
docker compose up -d sqrzl
curl --fail http://127.0.0.1:9000/healthz
```

Run the complete provider suite serially:

```console
dotnet test test/Pants.Tests/Pants.Tests.csproj \
  --configuration Release \
  --filter "Category=Sqrzl" \
  -- xUnit.MaxParallelThreads=1
```

These tests fail when Sqrzl is unavailable. They never skip. The suite covers each provider's object
contract, strict cloud commits, forced SST publication, complete local-cache loss, provider-backed
recovery, and mixed two- and three-location topologies.

Stop the emulator and remove its test data:

```console
docker compose down --volumes
```

If ports 9000 or 9001 are already in use, select unused host ports for this checkout. The API port
is shared by Compose and the test suite, so the tests cannot accidentally qualify a different Sqrzl
instance:

```console
export SQRZL_API_PORT=19000
export SQRZL_UI_PORT=19001
docker compose up -d sqrzl
curl --fail "http://127.0.0.1:${SQRZL_API_PORT}/healthz"
dotnet test test/Pants.Tests/Pants.Tests.csproj \
  --configuration Release \
  --filter "Category=Sqrzl" \
  -- xUnit.MaxParallelThreads=1
docker compose down --volumes
unset SQRZL_API_PORT SQRZL_UI_PORT
```

`SQRZL_ENDPOINT` may instead point the tests at an explicitly managed emulator. It takes
precedence over `SQRZL_API_PORT`.

The Ubuntu CI matrix job starts Sqrzl, waits for its health endpoint, and runs the complete test
suite, including every `Sqrzl` test. It always removes the Compose volume afterward. The macOS
and Windows jobs run the non-Sqrzl suite because those runners do not start the emulator.
