#!/usr/bin/env bash
# nopCommerce's source at the commit the plugin is built and tested against (README.md,
# "Development"): the plugin's projects reference its framework projects, as nopCommerce's
# documentation shows for a plugin. Shallow and sparse: only the projects the build reads, never
# kept in this repository.
#
#   scripts/nopcommerce.sh [<directory>]
#
# The directory: the argument, else $NOPCOMMERCE_DIR, else .nopcommerce/ in this repository (the
# build looks there too: Directory.Build.props). Nothing is fetched when it holds that commit.
set -euo pipefail

REPOSITORY=https://github.com/nopSolutions/nopCommerce
TAG=release-5.00.1
COMMIT=daed7a6ab14badc5920dac1cbc2b2c77dfc1066c

root=$(cd "$(dirname "$0")/.." && pwd)
dir=${1:-${NOPCOMMERCE_DIR:-$root/.nopcommerce}}

if [ -d "$dir/.git" ] && [ "$(git -C "$dir" rev-parse HEAD 2>/dev/null || true)" = "$COMMIT" ]; then
  echo "nopCommerce $TAG ($COMMIT) in $dir"
  exit 0
fi

rm -rf "$dir"
mkdir -p "$dir"
git -C "$dir" init -q
git -C "$dir" remote add origin "$REPOSITORY"
# A partial clone: the files of the checked-out paths only are downloaded.
git -C "$dir" config remote.origin.promisor true
git -C "$dir" config remote.origin.partialclonefilter blob:none
git -C "$dir" sparse-checkout set --no-cone \
  /global.json \
  /src/Directory.Build.props \
  /src/Libraries/ \
  /src/Presentation/Nop.Web.Framework/
git -C "$dir" fetch -q --depth=1 --filter=blob:none origin "$COMMIT"
git -C "$dir" checkout -q FETCH_HEAD

# The tag still names that commit (a tag can move: the commit cannot).
tagged=$(git ls-remote "$REPOSITORY" "refs/tags/$TAG" "refs/tags/$TAG^{}" | awk '{print $1}' | tail -n 1)
if [ "$(git -C "$dir" rev-parse HEAD)" != "$COMMIT" ] || [ "$tagged" != "$COMMIT" ]; then
  echo "nopCommerce: expected $TAG at $COMMIT, got HEAD $(git -C "$dir" rev-parse HEAD), tag $tagged" >&2
  exit 1
fi
echo "nopCommerce $TAG ($COMMIT) in $dir"
