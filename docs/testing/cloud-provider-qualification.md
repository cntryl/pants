# Cloud provider qualification

Sqrzl is the deterministic qualification environment for Pants cloud storage. The Compose service
uses the same pinned emulator image as Midge and provides S3, Azure Blob, GCS XML, and GCS JSON front
doors without live-cloud credentials.

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

The ordinary CI matrix excludes the `Sqrzl` category because those runners do not start the
emulator. A dedicated CI job starts Sqrzl, waits for its health endpoint, runs every Sqrzl test, and
always removes the Compose volume afterward. Live-cloud qualification remains separate because it
tests credentials and provider operations rather than deterministic protocol compatibility.
