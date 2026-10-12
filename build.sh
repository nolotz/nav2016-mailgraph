#!/usr/bin/env bash
# One-shot build for macOS/Linux (and Windows via Git Bash):
#   ./build.sh            build + unit tests + dist/Add-ins
#   ./build.sh --no-test  skip tests
#   ./build.sh --clean    remove bin/obj/dist first
set -euo pipefail
cd "$(dirname "$0")"

CONFIG=Release
RUN_TESTS=1
for arg in "$@"; do
  case "$arg" in
    --no-test) RUN_TESTS=0 ;;
    --clean) rm -rf dist; find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

TOOLS_TFM=$(sed -n 's:.*<ToolsTargetFramework>\(.*\)</ToolsTargetFramework>.*:\1:p' Directory.Build.props)
VERSION=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props)

echo "==> dotnet $(dotnet --version), tools TFM $TOOLS_TFM, version $VERSION"

# net8.0 test host / nmg do not roll forward to a newer major runtime: an SDK 10-only machine builds fine but
# cannot run the tests. Fail early with a clear message instead of a test-host error.
TOOLS_MAJOR=$(echo "$TOOLS_TFM" | sed -n 's/^net\([0-9][0-9]*\)\..*/\1/p')
RUNTIMES=$(dotnet --list-runtimes)
if [[ "$RUN_TESTS" == "1" && -n "$TOOLS_MAJOR" ]] && ! grep -q "^Microsoft.NETCore.App $TOOLS_MAJOR\." <<<"$RUNTIMES"; then
  echo "error: tests target $TOOLS_TFM but the Microsoft.NETCore.App $TOOLS_MAJOR.x runtime is not installed." >&2
  echo "       macOS: brew install --cask dotnet-sdk@$TOOLS_MAJOR   (or run ./build.sh --no-test)" >&2
  exit 1
fi
dotnet restore NavMailGraph.sln
dotnet build NavMailGraph.sln -c "$CONFIG" --no-restore

if [[ "$RUN_TESTS" == "1" ]]; then
  echo "==> tests ($TOOLS_TFM)"
  dotnet test tests/NavMailGraph.Tests/NavMailGraph.Tests.csproj -c "$CONFIG" -f "$TOOLS_TFM" --no-build
  if [[ "${OS:-}" == "Windows_NT" ]]; then
    echo "==> tests (net462, real .NET Framework runtime)"
    dotnet test tests/NavMailGraph.Tests/NavMailGraph.Tests.csproj -c "$CONFIG" -f net462 --no-build
  fi
fi

echo "==> dist/Add-ins (net462 payload for the NAV Server Add-ins folder)"
rm -rf dist
dotnet publish src/NavMailGraph/NavMailGraph.csproj -c "$CONFIG" -f net462 --no-build -o dist/Add-ins/NavMailGraph
dotnet publish src/NavMailGraph.Msal/NavMailGraph.Msal.csproj -c "$CONFIG" -f net462 --no-build -o dist/Add-ins/NavMailGraph/Msal
# the core DLL is already in the parent folder; NAV scans Add-ins recursively, so avoid shipping it twice
rm -f dist/Add-ins/NavMailGraph/Msal/NavMailGraph.dll dist/Add-ins/NavMailGraph/Msal/NavMailGraph.pdb dist/Add-ins/NavMailGraph/Msal/NavMailGraph.xml
find dist -name '*.pdb' -delete
mkdir -p dist/nav && cp -R nav/. dist/nav/
( cd dist && find Add-ins nav -type f | sort | xargs shasum -a 256 > SHA256SUMS )
echo "==> done: $(find dist/Add-ins -type f | wc -l | tr -d ' ') files in dist/Add-ins"
cat dist/SHA256SUMS
