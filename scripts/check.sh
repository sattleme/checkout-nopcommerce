#!/usr/bin/env bash
# Every check of the repository, as the CI runs them (README.md, "Development"): nopCommerce's
# source at its pinned commit, the pinned copies, the packages from the lockfiles, the coding
# standard, the build (analyzers, warnings as errors), the tests, the plugin's archive and what it
# holds, and the brand. The end-to-end test needs Docker: e2e/run.sh.
#
#   scripts/check.sh
#
# nopCommerce's source goes to $NOPCOMMERCE_DIR, or .nopcommerce/ in this repository.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
export CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NOPCOMMERCE_DIR=${NOPCOMMERCE_DIR:-$root/.nopcommerce}

echo "== nopCommerce's source"
scripts/nopcommerce.sh "$NOPCOMMERCE_DIR"

echo "== The pinned copies (sdk/, vendor/)"
dotnet run --project tools/Sattleme.Nop.Pins -c Release -- check

echo "== Packages, from the lockfiles"
dotnet restore Nop.Plugin.Payments.Sattleme.sln --locked-mode

echo "== Build (analyzers, warnings as errors), and the plugin's archive"
dotnet build Nop.Plugin.Payments.Sattleme.sln -c Release --no-restore

# After the build: the embedded projects compile copies the build writes (third-party code, not
# formatted here); nopCommerce's source, which the solution references, is nopCommerce's.
echo "== Coding standard"
dotnet format Nop.Plugin.Payments.Sattleme.sln --verify-no-changes --no-restore --exclude src/Embedded .nopcommerce

echo "== Tests"
dotnet test tests/Nop.Plugin.Payments.Sattleme.Tests -c Release --no-build

echo "== The archive (the tests check it file by file)"
archive=$(ls artifacts/Release/Payments.Sattleme-*.zip)
python3 -I -m zipfile -l "$archive"
if python3 -I -m zipfile -l "$archive" | grep -E '(Sattleme\.Checkout|NBitcoin\.Secp256k1)\.dll'; then
  echo "the archive holds an assembly of the original names" >&2
  exit 1
fi

echo "== Brand"
scripts/brand.sh

echo "All checks passed."
