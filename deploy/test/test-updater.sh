#!/usr/bin/env bash
# Tests the update script in a sandbox: fake GitHub API, fake Jellyfin "/health", fake systemctl.
# Nothing is installed on the machine. Requires: bash, python3, curl, jq. Usage: ./test-updater.sh
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
UPDATER="$HERE/../custom-metadata-updater.sh"
T="$(mktemp -d)"; PORT="${PORT:-18731}"
pass=0; fail=0
check() { if [ "$2" = 0 ]; then pass=$((pass+1)); echo "PASS $1"; else fail=$((fail+1)); echo "FAIL $1"; fi; }
cleanup() { kill "$SERVER_PID" 2>/dev/null; rm -rf "$T"; }
trap cleanup EXIT

mkdir -p "$T/plugin" "$T/state" "$T/dl" "$T/bin" "$T/cfg"
echo '<PluginConfiguration/>' > "$T/cfg/config.xml"

# fake systemctl: remembers "running / stopped" in a file
cat > "$T/bin/systemctl" <<SH
#!/usr/bin/env bash
case "\$1" in
  stop) echo stopped > "$T/svc" ;;
  start) echo running > "$T/svc" ;;
  is-active) [ "\$(cat "$T/svc" 2>/dev/null)" = running ] ;;
esac
SH
chmod +x "$T/bin/systemctl"; echo running > "$T/svc"

# fake GitHub (releases/latest and downloads) + fake /health: "Healthy" unless the installed DLL contains BAD
cat > "$T/server.py" <<PY
import http.server, os, json
T="$T"; PORT=$PORT
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self,*a): pass
    def do_GET(self):
        if self.path.endswith("/releases/latest"):
            d=open(T+"/release.json","rb").read(); self.send_response(200); self.send_header("Content-Type","application/json")
        elif self.path.startswith("/dl/"):
            f=T+"/dl/"+os.path.basename(self.path); 
            if not os.path.exists(f): self.send_response(404); self.end_headers(); return
            d=open(f,"rb").read(); self.send_response(200)
        elif self.path=="/health":
            dll=T+"/plugin/Jellyfin.Plugin.CustomMetadata.dll"
            bad=os.path.exists(dll) and b"BAD" in open(dll,"rb").read()
            d=b"Unhealthy" if bad else b"Healthy"; self.send_response(503 if bad else 200)
        else: self.send_response(404); self.end_headers(); return
        self.send_header("Content-Length",str(len(d))); self.end_headers(); self.wfile.write(d)
http.server.ThreadingHTTPServer(("127.0.0.1",PORT),H).serve_forever()
PY
python3 "$T/server.py" & SERVER_PID=$!
sleep 1

# builds a plausible "DLL" (MZ + over 100 KB) and a release; $3 = "bad" for a DLL that makes Jellyfin unhealthy,
# "badsum" for a wrong checksum
publish() {  # tag marker [bad|badsum]
  { printf 'MZ'; head -c 130000 /dev/urandom; printf '%s' "$2"; [ "${3:-}" = bad ] && printf 'BAD'; } > "$T/dl/Jellyfin.Plugin.CustomMetadata.dll"
  ( cd "$T/dl" && sha256sum Jellyfin.Plugin.CustomMetadata.dll > SHA256SUMS )
  [ "${3:-}" = badsum ] && echo "0000000000000000000000000000000000000000000000000000000000000000  Jellyfin.Plugin.CustomMetadata.dll" > "$T/dl/SHA256SUMS"
  jq -n --arg tag "$1" --arg base "http://127.0.0.1:$PORT/dl" '{tag_name:$tag, assets:[{name:"Jellyfin.Plugin.CustomMetadata.dll",browser_download_url:($base+"/Jellyfin.Plugin.CustomMetadata.dll")},{name:"SHA256SUMS",browser_download_url:($base+"/SHA256SUMS")}]}' > "$T/release.json"
}
update() {
  PATH="$T/bin:$PATH" GITHUB_REPO=o/r API_URL="http://127.0.0.1:$PORT" PLUGIN_DIR="$T/plugin" CONFIG_XML="$T/cfg/config.xml" \
  STATE_DIR="$T/state" JELLYFIN_URL="http://127.0.0.1:$PORT" HEALTH_TIMEOUT=6 SKIP_CHOWN=1 UPDATER_ENV=/nonexistent "$UPDATER" "$@" 2>&1
}
DLL="$T/plugin/Jellyfin.Plugin.CustomMetadata.dll"
sum() { sha256sum "$DLL" | cut -d' ' -f1; }

echo "--- 1. first installation (v0.0.1)"
publish v0.0.1 one; out="$(update)"; rc=$?
check "installed, exit code 0" $([ $rc = 0 ] && [ -f "$DLL" ] && echo 0 || echo 1)
check "version recorded" $([ "$(cat "$T/state/installed-version")" = 0.0.1 ] && echo 0 || echo 1)
v1="$(sum)"

echo "--- 2. already up to date"
out="$(update)"; check "nothing to do" $(echo "$out" | grep -q "up to date" && echo 0 || echo 1)

echo "--- 3. new healthy version (v0.0.2)"
publish v0.0.2 two; update >/dev/null; rc=$?
check "update installed" $([ $rc = 0 ] && [ "$(cat "$T/state/installed-version")" = 0.0.2 ] && echo 0 || echo 1)
check "previous DLL backed up" $(ls "$T/state/backups"/*/Jellyfin.Plugin.CustomMetadata.dll >/dev/null 2>&1 && echo 0 || echo 1)
check "configuration backed up" $(ls "$T/state/backups"/*/config.xml >/dev/null 2>&1 && echo 0 || echo 1)
v2="$(sum)"

echo "--- 4. version that makes Jellyfin unhealthy (v0.0.3): rollback expected"
publish v0.0.3 three bad; update >/dev/null; rc=$?
check "failure reported (exit code 1)" $([ $rc = 1 ] && echo 0 || echo 1)
check "previous DLL put back" $([ "$(sum)" = "$v2" ] && echo 0 || echo 1)
check "recorded version is 0.0.2" $([ "$(cat "$T/state/installed-version")" = 0.0.2 ] && echo 0 || echo 1)
check "0.0.3 set aside" $(grep -qx 0.0.3 "$T/state/failed-versions" && echo 0 || echo 1)
check "Jellyfin running again" $([ "$(cat "$T/svc")" = running ] && echo 0 || echo 1)

echo "--- 5. the failed version is not retried in a loop"
out="$(update)"; rc=$?
check "ignored, exit code 0" $([ $rc = 0 ] && echo "$out" | grep -q "already failed" && echo 0 || echo 1)

echo "--- 6. invalid SHA-256 checksum (v0.0.4)"
publish v0.0.4 four badsum; out="$(update)"; rc=$?
check "rejected" $([ $rc != 0 ] && echo "$out" | grep -q "checksum" && echo 0 || echo 1)
check "DLL unchanged" $([ "$(sum)" = "$v2" ] && echo 0 || echo 1)

echo "--- 7. unexpected tag"
publish latest five; out="$(update)"; rc=$?
check "rejected" $([ $rc != 0 ] && echo "$out" | grep -q "unexpected release tag" && echo 0 || echo 1)

echo "--- 8. --check changes nothing"
publish v0.0.5 five; out="$(update --check)"; rc=$?
check "announces the update without installing it" $([ $rc = 0 ] && echo "$out" | grep -q "update available" && [ "$(sum)" = "$v2" ] && echo 0 || echo 1)

echo "--- 9. --rollback"
out="$(update --rollback)"; rc=$?
check "rollback performed" $([ $rc = 0 ] && echo "$out" | grep -q "Jellyfin is healthy" && echo 0 || echo 1)

echo; echo "$pass PASS, $fail FAIL"; [ "$fail" = 0 ]
