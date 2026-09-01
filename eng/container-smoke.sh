#!/usr/bin/env bash
set -euo pipefail

image="${1:?image is required}"
evidence="${2:?evidence path is required}"
cid=""
cleanup() {
  if [[ -n "$cid" ]]; then docker rm --force "$cid" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

cid="$(docker run --detach --rm --publish 127.0.0.1::8080 \
  --env CalendarWarmup__Enabled=false \
  --env ProviderScheduler__Enabled=false \
  --env ProviderDeltaSync__Enabled=false \
  --env ImdbDataset__Enabled=false \
  --env CacheMaintenance__Enabled=false \
  "$image")"
port="$(docker port "$cid" 8080/tcp | sed 's/.*://')"

for attempt in $(seq 1 60); do
  if curl --fail --silent --show-error "http://127.0.0.1:$port/health/ready" > /tmp/premierecalendar-ready.txt; then
    break
  fi
  if [[ "$attempt" == 60 ]]; then docker logs "$cid"; exit 1; fi
  sleep 1
done

test "$(docker inspect --format '{{.Config.User}}' "$cid")" = "1654"
curl --fail --silent --show-error "http://127.0.0.1:$port/health/version" > /tmp/premierecalendar-version.json
curl --fail --silent --show-error "http://127.0.0.1:$port/_framework/blazor.web.js" > /tmp/premierecalendar-blazor.web.js
grep --quiet 'Blazor' /tmp/premierecalendar-blazor.web.js
curl --fail --silent --show-error "http://127.0.0.1:$port/" > /tmp/premierecalendar-root.html
grep --quiet '_framework/blazor.web.js' /tmp/premierecalendar-root.html
mkdir -p "$(dirname "$evidence")"
printf '{"schemaVersion":1,"image":"%s","containerUser":"1654","ready":"%s","version":%s,"rootBytes":%s,"blazorBytes":%s}\n' \
  "$image" "$(tr -d '\r\n' < /tmp/premierecalendar-ready.txt)" \
  "$(cat /tmp/premierecalendar-version.json)" \
  "$(wc -c < /tmp/premierecalendar-root.html)" \
  "$(wc -c < /tmp/premierecalendar-blazor.web.js)" > "$evidence"
