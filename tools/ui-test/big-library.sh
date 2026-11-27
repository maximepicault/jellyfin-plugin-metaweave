#!/usr/bin/env bash
# A large library for timing the theme's Index: a second Jellyfin 12.4 (cm-uiperf, http://localhost:8099,
# admin/admin) with 12 000 episodes and 600 films whose genres, tags, studios and cast come from NFO files. The videos
# are .strm files, so the scan does not probe 12 000 files.
#
#   ./big-library.sh          create (or restart) the container with the DLL built by ui-test.sh, and scan the library
#   ./big-library.sh --stop   remove the container (the media and the database stay in the work folder)
#
# Work data outside the repository: ${CM_PERF_WORK:-~/.cache/cm-ui-perf}
set -euo pipefail

WORK="${CM_PERF_WORK:-$HOME/.cache/cm-ui-perf}"
UI_WORK="${CM_UI_WORK:-$HOME/.cache/cm-ui-test}"
NAME=cm-uiperf
PORT=8099
J="http://localhost:$PORT"
IMAGE=jellyfin/jellyfin:12.4
H='Content-Type: application/json'

log() { printf '\n▶ %s\n' "$*"; }
wait_for() {
  local cmd="$1" timeout="${2:-120}" t=0
  until eval "$cmd" >/dev/null 2>&1; do
    sleep 2; t=$((t + 2))
    if [ "$t" -ge "$timeout" ]; then echo "⚠️ timeout: $cmd"; return 1; fi
  done
}

if [ "${1:-}" = "--stop" ]; then docker rm -f "$NAME" >/dev/null 2>&1 || true; echo "stopped"; exit 0; fi

DLL="$UI_WORK/build/bin/Release/net10.0/Jellyfin.Plugin.CustomMetadata.dll"
[ -f "$DLL" ] || { echo "DLL not found: run ui-test.sh once first"; exit 1; }
mkdir -p "$WORK"/{config/plugins/CustomMetadata,config/plugins/configurations,cache,media}

if [ ! -f "$WORK/media/.done" ]; then
  log "Generating 12 000 episodes and 600 films (.strm + .nfo)"
  python3 - "$WORK/media" <<'EOF'
import os, random, sys
from xml.sax.saxutils import escape
root = sys.argv[1]
rnd = random.Random(7)
genres = ['Genre %02d' % i for i in range(28)]
tags = ['Etiquette %03d' % i for i in range(400)]
frequent = tags[:25]
studios = ['Studio %02d' % i for i in range(15)]
people = ['Personne %03d' % i for i in range(900)]
def fields(cast, extra=('Commune',)):
    out = ''.join('<genre>%s</genre>' % g for g in rnd.sample(genres, 2))
    out += ''.join('<tag>%s</tag>' % escape(t) for t in dict.fromkeys(list(extra) + rnd.sample(tags, 2) + [rnd.choice(frequent)]))
    out += ''.join('<actor><name>%s</name></actor>' % p for p in rnd.sample(cast, 3))
    return out
def write(path, text):
    with open(path, 'w', encoding='utf-8') as f: f.write(text)
for s in range(120):
    name = 'Serie %03d' % s
    folder = os.path.join(root, 'series', name)
    cast = rnd.sample(people, 25)
    os.makedirs(folder, exist_ok=True)
    write(os.path.join(folder, 'tvshow.nfo'), '<tvshow><title>%s</title><year>%d</year><studio>%s</studio>%s</tvshow>' % (name, 1990 + s % 35, rnd.choice(studios), fields(cast)))
    for season in (1, 2):
        sf = os.path.join(folder, 'Season %02d' % season)
        os.makedirs(sf, exist_ok=True)
        for e in range(1, 51):
            base = os.path.join(sf, '%s S%02dE%02d' % (name, season, e))
            write(base + '.strm', 'http://127.0.0.1:9/video.mkv\n')
            year = 1990 + (s + season) % 35
            write(base + '.nfo', '<episodedetails><title>Episode %d de %s</title><season>%d</season><episode>%d</episode><year>%d</year><aired>%d-%02d-%02d</aired>%s</episodedetails>' % (e, name, season, e, year, year, 1 + e % 12, 1 + e % 28, fields(cast)))
for m in range(600):
    name = 'Film %03d (%d)' % (m, 1990 + m % 35)
    folder = os.path.join(root, 'films', name)
    os.makedirs(folder, exist_ok=True)
    write(os.path.join(folder, name + '.strm'), 'http://127.0.0.1:9/video.mkv\n')
    write(os.path.join(folder, name + '.nfo'), '<movie><title>Film %03d</title><year>%d</year><studio>%s</studio>%s</movie>' % (m, 1990 + m % 35, rnd.choice(studios), fields(people)))
open(os.path.join(root, '.done'), 'w').close()
EOF
fi

cp "$DLL" "$WORK/config/plugins/CustomMetadata/"
FRESH=0
if ! docker inspect "$NAME" >/dev/null 2>&1; then
  FRESH=1
  cat >"$WORK/config/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <EnableThemeScript>true</EnableThemeScript>
  <CreateVirtualEpisodes>false</CreateVirtualEpisodes>
</PluginConfiguration>
EOF
  log "Creating container $NAME on :$PORT"
  docker run -d --name "$NAME" --user "$(id -u):$(id -g)" -p "$PORT:8096" \
    -v "$WORK/config:/config" -v "$WORK/cache:/cache" -v "$WORK/media:/media" "$IMAGE" >/dev/null
else
  log "Restarting $NAME with the new DLL"
  docker restart "$NAME" >/dev/null
fi
wait_for "curl -sf -m 2 $J/System/Info/Public" 180

if [ "$FRESH" = 1 ]; then
  log "Startup wizard (admin / admin)"
  wait_for "curl -sf -X POST '$J/Startup/Configuration' -H '$H' -d '{\"UICulture\":\"fr\",\"MetadataCountryCode\":\"FR\",\"PreferredMetadataLanguage\":\"fr\"}'" 90
  wait_for "curl -sf '$J/Startup/User'" 60
  wait_for "curl -sf -X POST '$J/Startup/User' -H '$H' -d '{\"Name\":\"admin\",\"Password\":\"admin\"}'" 60
  wait_for "curl -sf -X POST '$J/Startup/RemoteAccess' -H '$H' -d '{\"EnableRemoteAccess\":true,\"EnableAutomaticPortMapping\":false}'" 60
  wait_for "curl -sf -X POST '$J/Startup/Complete'" 60
fi

login() {
  curl -sf -X POST "$J/Users/AuthenticateByName" -H "$H" \
    -H 'Authorization: MediaBrowser Client="perf", Device="cli", DeviceId="cm-ui-perf", Version="1.0"' \
    -d '{"Username":"admin","Pw":"admin"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])' 2>/dev/null
}
TOKEN=""
for _ in $(seq 1 45); do TOKEN=$(login || true); [ -n "$TOKEN" ] && break; sleep 2; done
[ -n "$TOKEN" ] || { echo "⚠️ login failed on $J"; exit 1; }
A="Authorization: MediaBrowser Token=\"$TOKEN\""

if [ "$(curl -s "$J/Library/VirtualFolders" -H "$A" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)))')" = 0 ]; then
  log "Libraries: NFO files only, no internet provider"
  for lib in "Series:tvshows:/media/series:Series,Season,Episode" "Films:movies:/media/films:Movie"; do
    IFS=: read -r name type path types <<<"$lib"
    python3 - "$path" "$types" >"$WORK/library.json" <<'EOF'
import json, sys
t = lambda typ: {"Type": typ, "MetadataFetchers": [], "MetadataFetcherOrder": [], "ImageFetchers": [], "ImageFetcherOrder": []}
print(json.dumps({"LibraryOptions": {"EnableRealtimeMonitor": False, "EnableChapterImageExtraction": False, "EnableTrickplayImageExtraction": False,
  "LocalMetadataReaderOrder": ["Nfo"], "MetadataSavers": [], "PathInfos": [{"Path": sys.argv[1]}], "TypeOptions": [t(x) for x in sys.argv[2].split(",")]}}))
EOF
    curl -sf -X POST "$J/Library/VirtualFolders?name=$name&collectionType=$type&refreshLibrary=false" -H "$A" -H "$H" --data-binary "@$WORK/library.json" >/dev/null
  done
  curl -sf -X POST "$J/Library/Refresh" -H "$A" >/dev/null
  sleep 10
fi

count() { curl -s "$J/Items?IncludeItemTypes=$1&Recursive=true&Limit=0&EnableTotalRecordCount=true" -H "$A" | python3 -c 'import json,sys;print(json.load(sys.stdin)["TotalRecordCount"])'; }
log "Waiting for the scan"
for _ in $(seq 1 270); do
  running=$(curl -s "$J/ScheduledTasks?isHidden=false" -H "$A" | grep -c '"State":"Running"' || true)
  [ "$running" = 0 ] && break
  sleep 10
done
echo "episodes: $(count Episode), films: $(count Movie), series: $(count Series)"
echo "✅ $J (admin / admin)"
