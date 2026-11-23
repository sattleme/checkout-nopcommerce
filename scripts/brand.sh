#!/usr/bin/env bash
# The brand (CONVENTIONS): sattle.me everywhere, the authors and the company of every manifest
# included; the merchant's legal entity, the former brand's name, only in a LICENSE's copyright
# line and in a .NET project's <Copyright>. Every other mention of the former brand fails. The
# pattern is written with brackets, so that this script does not name the brand it looks for. The
# pinned specification's copy is left out: its base64 may hold those letters by chance.
set -euo pipefail
cd "$(dirname "$0")/.."
if git grep -I -i -n -E '[s]wiss|[s]bp' -- . ':!spec/' ':!sdk/spec/' \
  | grep -v -E '^([^:]*/)?LICENSE:[0-9]+:Copyright \(c\) [0-9]{4} [^:]+$' \
  | grep -v -E '^[^:]+:[0-9]+:[[:space:]]*<Copyright>Copyright \(c\) [0-9]{4} [^<]+</Copyright>$'; then
  echo "a mention of the former brand (above)" >&2
  exit 1
fi
echo "Brand: sattle.me only."
