# Cloud provider qualification

Sqrzl is the deterministic qualification environment for Pants cloud storage. The Compose service
uses the same pinned emulator image as Midge and provides S3, Azure Blob, GCS XML, and GCS JSON front
doors without live-cloud credentials.

OCI configuration and request routing have deterministic structural and provider-shaped coverage,
but Sqrzl does not expose an OCI identity and no live OCI qualification is claimed. OCI production
qualification requires a separate credentialed run against OCI Object Storage.

Start the emulator:

```console
docker compose up -d sqrzl
curl --fail http://127.0.0.1:9001/healthz
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
export PANTS_SQRZL_API_PORT=19000
export PANTS_SQRZL_UI_PORT=19001
docker compose up -d sqrzl
curl --fail "http://127.0.0.1:${PANTS_SQRZL_API_PORT}/healthz"
dotnet test test/Pants.Tests/Pants.Tests.csproj \
  --configuration Release \
  --filter "Category=Sqrzl" \
  -- xUnit.MaxParallelThreads=1
docker compose down --volumes
unset PANTS_SQRZL_API_PORT PANTS_SQRZL_UI_PORT
```

`PANTS_SQRZL_ENDPOINT` may instead point the tests at an explicitly managed emulator. It takes
precedence over `PANTS_SQRZL_API_PORT`.

The ordinary CI matrix excludes the `Sqrzl` category because those runners do not start the
emulator. A dedicated CI job starts Sqrzl, waits for its health endpoint, runs every Sqrzl test, and
always removes the Compose volume afterward. Live-cloud qualification remains separate because it
tests credentials and provider operations rather than deterministic protocol compatibility.
