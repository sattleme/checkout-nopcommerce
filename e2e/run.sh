#!/usr/bin/env bash
# The end-to-end test (README.md, "Development"): nopCommerce's official image at a pinned digest,
# on PostgreSQL, in containers of their own; the plugin installed from its archive, with the
# test-only fake of the SDK (e2e/FakeSdk, never shipped); then e2e/drive.py does what a merchant
# and a customer do. Needs Docker, .NET and python3; nopCommerce's source for the build
# (scripts/nopcommerce.sh, or $NOPCOMMERCE_DIR).
#
#   e2e/run.sh                builds the archives (Release), runs, removes its containers and network
#   SKIP_BUILD=1 e2e/run.sh   uses the archives already in artifacts/Release/
#   E2E_KEEP=1 e2e/run.sh     keeps the containers and network of a failed run, to look at them
#                             (it prints their names and the shop's address; remove them after)
#
# It never touches containers, volumes or images that are not its own; the images it pulls are
# left for the next run (removed by the caller when done: docker image rm with the names below).
set -euo pipefail

NOP_IMAGE=nopcommerceteam/nopcommerce:5.00.1@sha256:732bd6caf8a1d9e17aef987d044629b1b5be9ef8afa215af9024116cd0036171
PG_IMAGE=postgres:17.11-alpine@sha256:e5d2edd28b1f8d40a0a6c0c9cdc545b19b56a0d5d88f94bc11a97d970d4700d0

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"

if [ "${SKIP_BUILD:-}" != "1" ]; then
  dotnet build src/Nop.Plugin.Payments.Sattleme -c Release
  dotnet build e2e/FakeSdk -c Release
fi
plugin=$(ls artifacts/Release/Payments.Sattleme-*.zip)
fake=artifacts/Release/Misc.SattlemeE2e.zip
test -f "$plugin" && test -f "$fake"

run="sattleme-nop-e2e-$(date +%s)-$$"
network="$run-net"
database="$run-pg"
shop="$run-shop"
password="pg-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "[e2e] nopCommerce's log (last lines):" >&2
    docker logs --tail 200 "$shop" >&2 2>&1 || true
  fi
  if [ "$status" -ne 0 ] && [ "${E2E_KEEP:-}" = "1" ]; then
    echo "[e2e] kept: the shop $shop at http://127.0.0.1:${port:-?}/ (Host: localhost), $database, $network" >&2
    echo "[e2e] remove: docker rm -f -v $shop $database && docker network rm $network" >&2
    exit "$status"
  fi
  docker rm -f -v "$shop" "$database" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  exit "$status"
}
trap cleanup EXIT

docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" \
  -e POSTGRES_PASSWORD="$password" "$PG_IMAGE" >/dev/null
# nopCommerce stops itself to restart (after its installation, after plugins are installed):
# Docker starts it again, on the same port (a port Docker chose would change at each restart).
port=$(python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')
docker run -d --name "$shop" --network "$network" --restart unless-stopped \
  -p "127.0.0.1:$port:80" "$NOP_IMAGE" >/dev/null

for _ in $(seq 1 60); do
  if docker exec "$database" pg_isready -U postgres >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

python3 -I e2e/drive.py --base "http://127.0.0.1:$port" --host localhost \
  --pg-host "$database" --pg-password "$password" --plugin "$plugin" --fake "$fake"

# Uninstalled, the plugin left no table behind (its migration's Down).
tables=$(docker exec "$database" psql -U postgres -d nopcommerce -At \
  -c "select table_name from information_schema.tables where table_name ilike 'sattleme%'")
if [ -n "$tables" ]; then
  echo "[e2e] tables left after uninstalling the plugin: $tables" >&2
  exit 1
fi

# Nothing went wrong in the plugin meanwhile: nopCommerce's log (System, Log) holds no error of it
# (a view that does not compile, an exception), but the 404 nopCommerce logs for the payment page
# asked without its secret, which the test does on purpose.
errors=$(docker exec "$database" psql -U postgres -d nopcommerce -At \
  -c "select \"ShortMessage\" from \"Log\" where \"LogLevelId\" >= 40 and \"ShortMessage\" ilike '%sattle%'
      and \"ShortMessage\" not like 'Error 404. The requested page (/sattleme/pay/%'")
if [ -n "$errors" ]; then
  echo "[e2e] the plugin logged errors: $errors" >&2
  exit 1
fi
echo "[e2e] all good"
