# Cloud object read identity

Opening a cloud SST records the object size and version from one metadata read. Every later range
read for that source must refer to that same version. Built-in adapters express the condition in
the provider protocol: S3 and Azure use the recorded ETag with `If-Match`, and GCS uses the
recorded generation.

A provider precondition failure means the object changed while the source was being read and is
reported as `PantsBusyException`. Pants must not combine ranges from different object versions or
classify that race as stored-data corruption. A replacement after all requested ranges have
successfully completed does not invalidate the bytes already returned.

Each successful range response must be HTTP 206 and its `Content-Range` must match the requested
start and end, with a known total equal to the size recorded at open. When the manifest supplies a
nonzero SST size, that size must also match the metadata read. Invalid totals are rejected before
the response body is consumed.
