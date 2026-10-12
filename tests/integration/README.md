# Storage integration tests

End-to-end tests of `ObjectStorageClient` against local emulators:

| Service | Image | Endpoint | Credentials (test only) |
|---|---|---|---|
| Azure Blob (Azurite) | `mcr.microsoft.com/azure-storage/azurite:3.37.0` | `http://127.0.0.1:10000/devstoreaccount1` | account `devstoreaccount1`, Microsoft's public well-known key `Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==` |
| S3 (SeaweedFS) | `chrislusf/seaweedfs:4.48` | `http://127.0.0.1:8333` (path-style, region `us-east-1`) | access key `nmgtestaccesskey`, secret `nmgtestsecretkey0123456789` (from `s3-identities.json`) |

Why SeaweedFS: the `minio/minio` image is no longer pullable (see docs/research-notes-v2.md). SeaweedFS 4.48 was
checked here with SigV4 header auth, multipart upload, presigned GET, ListObjectsV2 paging, and a readable
`SignatureDoesNotMatch` for a wrong secret. Both services bind to 127.0.0.1 only and keep their data in memory or tmpfs,
so nothing survives `docker compose down`.

## Run

From the repository root:

```bash
docker compose -f tests/integration/docker-compose.yml up -d
NMG_INTEGRATION=1 dotnet test tests/NavMailGraph.Tests -c Release -f net8.0 --filter Category=Integration
docker compose -f tests/integration/docker-compose.yml down
```

You can also run `tests/integration/run.sh`. It starts the containers, waits until both answer, runs the tests and stops
the containers. Pass `--keep` to leave them running.

PowerShell:

```powershell
docker compose -f tests/integration/docker-compose.yml up -d
$env:NMG_INTEGRATION = "1"; dotnet test tests/NavMailGraph.Tests -c Release -f net8.0 --filter Category=Integration; Remove-Item Env:NMG_INTEGRATION
docker compose -f tests/integration/docker-compose.yml down
```

Without `NMG_INTEGRATION=1` the tests are reported as skipped, so the normal `dotnet test` run needs no Docker.

Each test run creates its own container or bucket (`nmg-it-<time>-<random>`), and each test uses its own key prefix.
To point the tests at other servers, set these environment variables:
`NMG_AZURITE_URL`, `NMG_S3_URL`, `NMG_S3_ACCESS_KEY`, `NMG_S3_SECRET_KEY`, `NMG_S3_REGION`.

## Coverage

These scenarios run for Azure Shared Key, Azure SAS URL (container SAS `sr=c`, `sp=racwdl`) and S3:

* a small upload and download roundtrip with a non-ASCII key containing special characters, followed by exists, size
  and delete;
* a 20 MiB file with the threshold and part size set to 5 MiB, uploaded as 5 Put Blocks plus Put Block List, or as an S3
  multipart upload; the download is streamed to a file and its SHA-256 is compared;
* a non-seekable stream of unknown length, uploaded through the internal `UploadFromStreamAsync` one part at a time
  (this is the path used for the Graph `$value` pipe);
* exists, size and list with paging (page size 2, following NextMarker or the continuation token), deleting a missing
  object, and TestConnection;
* `GetDownloadUrl` (S3 presigned GET, Azure service SAS, or the configured SAS), downloaded with a plain `HttpClient`.

Error tests:

* a wrong Azure key and a wrong S3 secret, plus an unknown access key, each giving a readable error text with a hint;
* a missing container or bucket, which gives `ContainerNotFound` or `NoSuchBucket` with a hint.

Note that Azurite 3.37.0 reports a wrong Shared Key signature as `AuthorizationFailure` ("Server failed to authenticate
the request..."). Azure itself documents `AuthenticationFailed`. Both get the same hint.

## Troubleshooting

* If S3 returns `HTTP 500 InternalError` and the SeaweedFS log says "No writable volumes", the volume server is out of
  slots. Every bucket is its own collection. The compose file uses `-volume.max=0` (automatic), which avoids this.
* If ports 10000 or 8333 are already in use, stop the other emulator or change the host ports in the compose file and set
  `NMG_AZURITE_URL` / `NMG_S3_URL` to match.
