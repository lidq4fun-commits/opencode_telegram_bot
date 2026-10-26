#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
ARCHIVE="$ROOT/dependencies/archives/git-2.56.0.tar.xz"
EXPECTED=26c56c296b38c0695b26fa95f475f1d01704d2d38e73465ca30b0b2f5dc789d3
[[ "$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')" == "$EXPECTED" ]] || { echo 'Git source checksum mismatch.' >&2; exit 1; }
STAGE="$(mktemp -d "$ROOT/dependencies/git-build.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
tar -xJf "$ARCHIVE" --strip-components=1 -C "$STAGE"
DEST="$ROOT/dependencies/payload/dependencies/git"
cd "$STAGE"
export MACOSX_DEPLOYMENT_TARGET=14.0
# Link only to macOS-provided libraries; do not depend on Homebrew paths.
make -j "$(sysctl -n hw.ncpu)" prefix="$DEST" NO_RUST=YesPlease NO_GETTEXT=YesPlease NO_TCLTK=YesPlease NO_OPENSSL=YesPlease NO_EXPAT=YesPlease NO_INSTALL_HARDLINKS=YesPlease install
cp COPYING "$ROOT/dependencies/payload/dependencies/licenses/Git-COPYING.txt"
mkdir -p "$ROOT/dependencies/payload/dependencies/git-source"
cp "$ARCHIVE" "$ROOT/dependencies/payload/dependencies/git-source/"
cp "$ROOT/Prepare-Git.sh" "$ROOT/dependencies/payload/dependencies/git-source/"
"$DEST/bin/git" --version
otool -L "$DEST/bin/git"
echo 'Portable Git built with matching source and license.'
