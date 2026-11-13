#!/usr/bin/env bash
# Visual test bench of the plugin: Jellyfin 10.11.6 in Docker + fake API + screenshots of the missing-episodes screen.
#
#   ./ui-test.sh            build + deploy + "Tout retraiter" analysis + screenshots
#   ./ui-test.sh --no-build redeploys the DLL already built
#   ./ui-test.sh --stop     stops the container and the fake API
#
# Test Jellyfin: http://localhost:8097 (admin / admin). The container on port 8096 belongs to ANOTHER project: do not touch it.
# Work data OUTSIDE the repository (the .csproj would compile the *.cs of a copy placed inside it):
#   ${CM_UI_WORK:-~/.cache/cm-ui-test}/{build,config,cache,media,img,shots}
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${CM_UI_WORK:-$HOME/.cache/cm-ui-test}"
NAME=cm-uitest
PORT=8097
API_PORT=8765
API_HOST=172.17.0.1            # docker0 gateway: the host as seen from the container
J="http://localhost:$PORT"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
IMAGE=jellyfin/jellyfin:10.11.6

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

mkdir -p "$WORK"/{config/plugins/CustomMetadata,config/plugins/configurations,cache,shots}

if [ "${1:-}" != "--no-build" ]; then
  log "Build Release"
  rsync -a --delete --exclude bin --exclude obj --exclude tools "$REPO/" "$WORK/build/"
  (cd "$WORK/build" && DOTNET_CLI_HOME="$WORK/.dotnet-home" NUGET_PACKAGES="$WORK/.nuget" "$DOTNET" build -c Release 2>&1 | grep -E " error |réussi|succeeded|Avertissement|Warn" | tail -3)
fi
DLL="$WORK/build/bin/Release/net9.0/Jellyfin.Plugin.CustomMetadata.dll"
[ -f "$DLL" ] || { echo "DLL introuvable : $DLL"; exit 1; }

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
    -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media:ro" "$IMAGE" >/dev/null
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

TOKEN=$(curl -sf -X POST "$J/Users/AuthenticateByName" -H "$H" \
  -H 'Authorization: MediaBrowser Client="ui-test", Device="cli", DeviceId="cm-ui-test", Version="1.0"' \
  -d '{"Username":"admin","Pw":"admin"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')
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
echo; echo "✅ Test Jellyfin: $J (admin / admin) — stop with: $0 --stop"
