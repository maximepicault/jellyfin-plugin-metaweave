#!/usr/bin/env bash
# Visual test bench of the plugin: Jellyfin 12.4 in Docker + fake API + screenshots of the missing-episodes screen.
#
#   ./ui-test.sh            build + deploy + "Tout retraiter" analysis + screenshots
#   ./ui-test.sh --no-build redeploys the DLL already built
#   ./ui-test.sh --fast     build + swap the DLL + restart the container, then stop: no media, no analysis, no screenshots
#                           (the inner loop for an HTML/C# change; the container and its library must already exist)
#   ./ui-test.sh --stop     stops the container and the fake API
#
# Test Jellyfin: http://localhost:8097 (admin / admin). The container on port 8096 belongs to ANOTHER project: do not touch it.
# Work data OUTSIDE the repository (the .csproj would compile the *.cs of a copy placed inside it):
#   ${CM_UI_WORK:-~/.cache/cm-ui-test}/{build,config,cache,media,img,shots}
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${CM_UI_WORK:-$HOME/.cache/cm-ui-test}"
# a second bench beside the main one (another server version): CM_UI_IMAGE, CM_UI_NAME, CM_UI_PORT and CM_UI_WORK together
NAME="${CM_UI_NAME:-cm-uitest}"
PORT="${CM_UI_PORT:-8097}"
API_PORT=8765
API_HOST=172.17.0.1            # docker0 gateway: the host as seen from the container
J="http://localhost:$PORT"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
IMAGE="${CM_UI_IMAGE:-jellyfin/jellyfin:12.4}"

log() { printf '\n▶ %s\n' "$*"; }
wait_for() {
  local cmd="$1" timeout="${2:-120}" t=0
  until eval "$cmd" >/dev/null 2>&1; do
    sleep 2; t=$((t + 2))
    if [ "$t" -ge "$timeout" ]; then echo "⚠️ timeout: $cmd"; return 1; fi   # no "[ ] && …": set -e would exit
  done
}

if [ "${1:-}" = "--stop" ]; then
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  [ -f "$WORK/api.pid" ] && kill "$(cat "$WORK/api.pid")" 2>/dev/null || true
  rm -f "$WORK/api.pid"; echo "stopped"; exit 0
fi

# A container of another server version would load neither this DLL nor, later, this configuration: say so instead of reusing it
RUNNING="$(docker inspect -f '{{.Config.Image}}' "$NAME" 2>/dev/null || true)"
if [ -n "$RUNNING" ] && [ "$RUNNING" != "$IMAGE" ]; then
  echo "Container $NAME runs $RUNNING, not $IMAGE. Remove it (docker rm -f $NAME) and use an empty work folder (now: $WORK),"
  echo "or run a second bench with CM_UI_NAME, CM_UI_PORT and CM_UI_WORK."
  exit 1
fi

mkdir -p "$WORK"/{config/plugins/CustomMetadata,config/plugins/configurations,cache,shots}

if [ "${1:-}" != "--no-build" ]; then
  log "Build Release"
  rsync -a --delete --exclude bin --exclude obj --exclude tools "$REPO/" "$WORK/build/"
  (cd "$WORK/build" && DOTNET_CLI_HOME="$WORK/.dotnet-home" NUGET_PACKAGES="$WORK/.nuget" "$DOTNET" build -c Release 2>&1 | grep -E " error |réussi|succeeded|Avertissement|Warn" | tail -3)
fi
DLL="$WORK/build/bin/Release/net10.0/Jellyfin.Plugin.CustomMetadata.dll"
[ -f "$DLL" ] || { echo "DLL introuvable : $DLL"; exit 1; }

# --fast: the quickest way to try a change. The test data, the fake API and the library are already there from a previous full run.
if [ "${1:-}" = "--fast" ]; then
  docker inspect "$NAME" >/dev/null 2>&1 || { echo "No container yet: run ./ui-test.sh once without --fast"; exit 1; }
  curl -sf -m 2 "http://127.0.0.1:$API_PORT/series/aventuriers" >/dev/null || { echo "Fake API is down: run ./ui-test.sh once without --fast"; exit 1; }
  cp "$DLL" "$WORK/config/plugins/CustomMetadata/"
  log "Restarting $NAME with the new DLL"
  docker restart "$NAME" >/dev/null
  wait_for "curl -sf -m 2 $J/System/Info/Public" 180
  # The web client answers before the server accepts logins: wait for a real login
  wait_for "curl -sf -X POST $J/Users/AuthenticateByName -H 'Content-Type: application/json' -H 'Authorization: MediaBrowser Client=\"ui-test\", Device=\"cli\", DeviceId=\"cm-ui-test\", Version=\"1.0\"' -d '{\"Username\":\"admin\",\"Pw\":\"admin\"}'" 90
  curl -s -m 60 -o /dev/null "$J/web/index.html" || true
  echo; echo "✅ Redeployed (fast): $J (admin / admin)"; exit 0
fi

log "Tagged media + fake API images"
python3 "$HERE/gen.py" "$WORK"

log "Fausse API sur :$API_PORT"
if ! curl -sf -m 2 "http://127.0.0.1:$API_PORT/series/aventuriers" >/dev/null; then
  nohup python3 "$HERE/server.py" "http://$API_HOST:$API_PORT" "$WORK/img" >"$WORK/api.log" 2>&1 &
  echo $! >"$WORK/api.pid"
  wait_for "curl -sf -m 1 http://127.0.0.1:$API_PORT/series/aventuriers" 15
fi

cp "$DLL" "$WORK/config/plugins/CustomMetadata/"
FRESH=0
if ! docker inspect "$NAME" >/dev/null 2>&1; then
  FRESH=1
  cat >"$WORK/config/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <EnableMetadata>true</EnableMetadata>
  <EnableImages>true</EnableImages>
  <EnableCache>false</EnableCache>
  <CreateVirtualEpisodes>true</CreateVirtualEpisodes>
  <Sources>
    <SourceConfiguration>
      <Name>Démo API</Name>
      <Prefix>dm</Prefix>
      <BaseUrl>http://$API_HOST:$API_PORT</BaseUrl>
      <Enabled>true</Enabled>
      <EpisodeListEndpoint>/sources/{id}/videos</EpisodeListEndpoint>
      <MaxRetries>0</MaxRetries>
    </SourceConfiguration>
  </Sources>
</PluginConfiguration>
EOF
  log "Creating container $NAME ($IMAGE) sur :$PORT"
  docker run -d --name "$NAME" --user "$(id -u):$(id -g)" -p "$PORT:8096" \
    -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media" "$IMAGE" >/dev/null
else
  log "Restarting $NAME with the new DLL"
  docker restart "$NAME" >/dev/null
fi
wait_for "curl -sf -m 2 $J/System/Info/Public" 180

H='Content-Type: application/json'
# Right after a restart, /System/Info/Public may briefly report StartupWizardCompleted=false:
# the wizard of an existing container is only rerun if it is still true a few seconds later
wizard_pending() { curl -s "$J/System/Info/Public" | grep -qi '"startupWizardCompleted":false'; }
if [ "$FRESH" = 1 ] || { wizard_pending && sleep 8 && wizard_pending; }; then
  log "Startup wizard (admin / admin)"
  # Retries: /System/Info answers before the wizard API is ready
  wait_for "curl -sf -X POST '$J/Startup/Configuration' -H '$H' -d '{\"UICulture\":\"fr\",\"MetadataCountryCode\":\"FR\",\"PreferredMetadataLanguage\":\"fr\"}'" 90
  wait_for "curl -sf '$J/Startup/User'" 60
  wait_for "curl -sf -X POST '$J/Startup/User' -H '$H' -d '{\"Name\":\"admin\",\"Password\":\"admin\"}'" 60
  wait_for "curl -sf -X POST '$J/Startup/RemoteAccess' -H '$H' -d '{\"EnableRemoteAccess\":true,\"EnableAutomaticPortMapping\":false}'" 60
  wait_for "curl -sf -X POST '$J/Startup/Complete'" 60
fi

# Right after a restart the server answers /System/Info/Public before it accepts logins (503): retry
login() {
  curl -sf -X POST "$J/Users/AuthenticateByName" -H "$H" \
    -H 'Authorization: MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-ui-test", Version="1.0"' \
    -d '{"Username":"admin","Pw":"admin"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])' 2>/dev/null
}
TOKEN=""
for _ in $(seq 1 45); do TOKEN=$(login || true); [ -n "$TOKEN" ] && break; sleep 2; done
[ -n "$TOKEN" ] || { echo "⚠️ login failed (admin / admin) on $J"; exit 1; }
A="Authorization: MediaBrowser Token=\"$TOKEN\""
count() { curl -s "$J/Items?IncludeItemTypes=$1&Recursive=true&Limit=0" -H "$A" | python3 -c 'import json,sys;print(json.load(sys.stdin)["TotalRecordCount"])'; }
tasks_idle() { ! curl -s "$J/ScheduledTasks?isHidden=false" -H "$A" | grep -q '"State":"Running"'; }

if [ "$(curl -s "$J/Library/VirtualFolders" -H "$A" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)))')" = 0 ]; then
  log "Series library (/media/series) limited to our providers"
  python3 - >"$WORK/library.json" <<'EOF'
import json
t = lambda typ: {"Type": typ, "MetadataFetchers": ["Custom Metadata Provider"], "MetadataFetcherOrder": ["Custom Metadata Provider"],
                 "ImageFetchers": ["Custom Image Provider"], "ImageFetcherOrder": ["Custom Image Provider"]}
print(json.dumps({"LibraryOptions": {"EnableRealtimeMonitor": False, "EnableChapterImageExtraction": False,
  "PathInfos": [{"Path": "/media/series"}], "TypeOptions": [t("Series"), t("Season"), t("Episode")], "SaveLocalMetadata": False}}))
EOF
  curl -sf -X POST "$J/Library/VirtualFolders?name=S%C3%A9ries&collectionType=tvshows&refreshLibrary=true" -H "$A" -H "$H" --data @"$WORK/library.json" >/dev/null
  wait_for '[ "$(count Episode)" -ge 10 ]' 240
fi
sleep 3; wait_for tasks_idle 240

log "Missing-episode analysis (Tout retraiter)"
curl -sf -X POST "$J/CustomMetadata/MissingEpisodes/Analyze?full=true" -H "$A" >/dev/null
sleep 4
wait_for "curl -s '$J/CustomMetadata/MissingEpisodes' -H '$A' | grep -q '\"Running\":false,\"Report\":{'" 300
curl -s "$J/CustomMetadata/MissingEpisodes" -H "$A" | python3 -c '
import json,sys; r=json.load(sys.stdin)["Report"]
print("  created %s, updated %s, unchanged %s, removed %s" % (r.get("Created"), r.get("Updated"), r.get("Unchanged"), r.get("Removed")))
for s in r["Seasons"]: print("  %-22s / %-18s %2s dans l API, %2s manquants %s" % (s["SeriesName"], s["SeasonName"], s.get("ApiCount", 0), len(s.get("Missing") or []), s.get("Error") or ""))'

log "Captures → $WORK/shots"
node "$HERE/shoot.mjs" "$J" "$WORK/shots"
# Safety net: a headless Chrome of an earlier, interrupted run (and its profile folder) must not pile up
pkill -f -- "--remote-debugging-port=.*--user-data-dir=$WORK" 2>/dev/null || true
sleep 1
find "$WORK" -maxdepth 3 -type d -name '.profile-*' -prune -exec rm -rf {} + 2>/dev/null || true
echo; echo "✅ Test Jellyfin: $J (admin / admin) — stop with: $0 --stop"
