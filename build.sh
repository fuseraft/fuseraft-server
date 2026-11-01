#!/usr/bin/env bash
# fuseraft-web build (Linux/macOS)
#
#   ./build.sh                         build + test
#   ./build.sh publish                 framework-dependent publish to artifacts/publish/portable/
#   ./build.sh publish linux-x64       self-contained publish for one platform
#   ./build.sh pack linux-x64          publish, then archive to artifacts/fuseraft-web-<version>-<rid>.tar.gz|.zip
#
# Set CONFIGURATION=Debug to build Debug. The version comes from git tags (MinVer, v-prefixed).

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

command -v dotnet >/dev/null || { echo "ERROR: 'dotnet' not found. Install the .NET 10 SDK from https://dot.net" >&2; exit 1; }

TARGET="${1:-test}"
RID="${2:-}"
CONFIGURATION="${CONFIGURATION:-Release}"
OUT="artifacts/publish/${RID:-portable}"

build() { dotnet build fuseraft-web.slnx -c "$CONFIGURATION" --nologo; }

test_() { dotnet test fuseraft-web.slnx -c "$CONFIGURATION" --nologo; }

publish() {
  rm -rf "$OUT"
  if [[ -n "$RID" ]]; then
    dotnet publish FuseraftWeb.csproj -c "$CONFIGURATION" -r "$RID" --self-contained -o "$OUT" --nologo \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
  else
    dotnet publish FuseraftWeb.csproj -c "$CONFIGURATION" -o "$OUT" --nologo -p:DebugType=none
  fi
  echo "Published to $OUT"
}

pack() {
  [[ -n "$RID" ]] || { echo "ERROR: pack needs a runtime, e.g. ./build.sh pack linux-x64" >&2; exit 1; }
  publish
  local version
  version="$(dotnet msbuild FuseraftWeb.csproj -t:MinVer -getProperty:MinVerVersion -p:Configuration="$CONFIGURATION" | tail -n1)"
  local name="fuseraft-web-$version-$RID"
  mkdir -p artifacts
  if [[ "$RID" == win-* ]]; then
    (cd "$OUT" && zip -qr "../../$name.zip" .)
    echo "Packed artifacts/$name.zip"
  else
    tar -czf "artifacts/$name.tar.gz" -C "$OUT" .
    echo "Packed artifacts/$name.tar.gz"
  fi
}

case "$TARGET" in
  build)   build ;;
  test)    build && test_ ;;
  publish) publish ;;
  pack)    pack ;;
  *) echo "Unknown target '$TARGET' (build | test | publish [rid] | pack <rid>)" >&2; exit 1 ;;
esac
