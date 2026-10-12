#!/usr/bin/env bash
# Starts the storage emulators, runs the integration tests (net8.0) and stops the emulators again.
# Usage: tests/integration/run.sh [--keep]   (--keep leaves the containers running)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
keep="${1:-}"

docker compose -f "$here/docker-compose.yml" up -d

wait_for() { # name url expected-status... (same function as in .github/workflows/ci.yml)
  local name="$1" url="$2"; shift 2
  for _ in $(seq 1 90); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "$url" || true)"
    for want in "$@"; do
      if [ "$code" = "$want" ]; then echo "$name ready ($code)"; return 0; fi
    done
    sleep 1
  done
  echo "$name did not become ready ($url -> $code)" >&2
  docker compose -f "$here/docker-compose.yml" logs --tail 50 >&2
  return 1
}
# Anonymous requests are rejected once the services are up: Azurite 400/403, SeaweedFS S3 403.
wait_for azurite "http://127.0.0.1:10000/devstoreaccount1?comp=list" 403 400
wait_for seaweedfs "http://127.0.0.1:8333/" 403

status=0
NMG_INTEGRATION=1 dotnet test "$repo/tests/NavMailGraph.Tests" -c Release -f net8.0 --filter Category=Integration || status=$?

if [ "$keep" != "--keep" ]; then
  docker compose -f "$here/docker-compose.yml" down
fi
exit $status
