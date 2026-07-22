#!/bin/bash
# build_hist.sh <TAG> <BUILDID>  — build a historical verifier from a Core release tag.
# Only the ANN scorer differs per era; platform files get the generic no-op shims.
set -e
TAG="$1"; BID="$2"
QUBIC="${QUBIC_SRC:-/home/claude/qubic}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TREE="$HERE/coretree_$BID"

rm -rf "$TREE"; mkdir -p "$TREE"
git -C "$QUBIC" archive "$TAG" src lib test | tar -x -C "$TREE"

# generic shims (logging/locks/profiling are cosmetic and stable across versions)
cp "$HERE/shim/platform/console_logging.h" "$TREE/src/platform/console_logging.h"
cp "$HERE/shim/platform/concurrency.h"     "$TREE/src/platform/concurrency.h"
cp "$HERE/shim/platform/profiling.h"       "$TREE/src/platform/profiling.h"
# 16-bit wchar for the wide-string filename arrays
sed -i -E 's/static unsigned short ([A-Z0-9_]+\[\] *= *L")/static wchar_t \1/' "$TREE/src/public_settings.h"

clang++ -std=c++17 -O2 -mavx2 -mbmi -mbmi2 -mlzcnt -mpopcnt -mrdrnd -fshort-wchar \
  -DNO_UEFI -DNDEBUG -w \
  -I"$TREE/src" -I"$TREE" \
  -I"$TREE/lib/platform_common" -I"$TREE/lib/platform_os" -I"$TREE/lib/platform_efi" \
  "$HERE/verifier_hist.cpp" -o "$HERE/verifier-$BID"
echo "built verifier-$BID from $TAG"
