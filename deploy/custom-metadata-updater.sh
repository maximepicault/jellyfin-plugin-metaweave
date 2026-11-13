#!/usr/bin/env bash
# Updates the "Custom Metadata" Jellyfin plugin from the repository's latest GitHub release.
#
# The server PULLS the release (outgoing requests only): no port to open, no access to grant to GitHub.
# Run as root, normally from the systemd timer (see deploy/README.md).
#
# Flow: latest release -> tag shape and SHA-256 check -> backup of the current DLL and configuration -> stop Jellyfin
# -> install -> start -> wait for /health. If Jellyfin does not become healthy again, the previous DLL is put back
# and the version is set aside (it is not retried in a loop).
#
# Options:  --check      say whether an update exists, install nothing
#           --force      reinstall the latest release even if already installed or previously failed
#           --rollback   restore the most recent backup
#           --dry-run    show what would be done
set -uo pipefail

ENV_FILE="${UPDATER_ENV:-/etc/custom-metadata-updater.env}"
# shellcheck disable=SC1090
[ -f "$ENV_FILE" ] && . "$ENV_FILE"

GITHUB_REPO="${GITHUB_REPO:-}"                                   # "owner/repository"
API_URL="${API_URL:-https://api.github.com}"
PLUGIN_DIR="${PLUGIN_DIR:-/var/lib/jellyfin/plugins/CustomMetadata}"
CONFIG_XML="${CONFIG_XML:-/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.CustomMetadata.xml}"
STATE_DIR="${STATE_DIR:-/var/lib/custom-metadata-updater}"
JELLYFIN_SERVICE="${JELLYFIN_SERVICE:-jellyfin}"
JELLYFIN_URL="${JELLYFIN_URL:-http://127.0.0.1:8096}"
JELLYFIN_USER="${JELLYFIN_USER:-jellyfin}"
JELLYFIN_GROUP="${JELLYFIN_GROUP:-jellyfin}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-120}"                          # seconds to wait for /health after starting
KEEP_BACKUPS="${KEEP_BACKUPS:-5}"
NOTIFY_ONLY="${NOTIFY_ONLY:-0}"                                  # 1 = only report (same effect as --check)
SYSTEMCTL="${SYSTEMCTL:-systemctl}"
SKIP_CHOWN="${SKIP_CHOWN:-0}"                                    # tests only
DLL="Jellyfin.Plugin.CustomMetadata.dll"

MODE="update"; DRY=0; FORCE=0
for arg in "$@"; do
  case "$arg" in
    --check) MODE="check" ;;
    --force) FORCE=1 ;;
    --rollback) MODE="rollback" ;;
    --dry-run) DRY=1 ;;
    *) echo "Unknown option: $arg"; exit 2 ;;
  esac
done
[ "$NOTIFY_ONLY" = 1 ] && [ "$MODE" = "update" ] && MODE="check"

log() { printf '[custom-metadata-updater] %s\n' "$*"; }
die() { log "ERROR: $*"; exit 1; }
run() { if [ "$DRY" = 1 ]; then log "[dry run] $*"; else "$@"; fi; }

for cmd in curl jq sha256sum flock install; do
  command -v "$cmd" >/dev/null 2>&1 || die "required command not found: $cmd"
done
[ "$MODE" = "rollback" ] || [ -n "$GITHUB_REPO" ] || die "GITHUB_REPO is not set (see $ENV_FILE)"

mkdir -p "$STATE_DIR/backups"
exec 9>"$STATE_DIR/lock"
flock -n 9 || { log "another run is in progress, exiting"; exit 0; }

installed_version() { cat "$STATE_DIR/installed-version" 2>/dev/null || echo "unknown"; }

wait_healthy() {
  local waited=0
  while [ "$waited" -lt "$HEALTH_TIMEOUT" ]; do
    if "$SYSTEMCTL" is-active --quiet "$JELLYFIN_SERVICE" \
       && [ "$(curl -fsS -m 5 "$JELLYFIN_URL/health" 2>/dev/null)" = "Healthy" ]; then
      return 0
    fi
    sleep 3; waited=$((waited + 3))
  done
  return 1
}

install_dll() {  # $1 = source file
  if [ "$SKIP_CHOWN" = 1 ]; then
    install -m 644 "$1" "$PLUGIN_DIR/$DLL"
  else
    install -o "$JELLYFIN_USER" -g "$JELLYFIN_GROUP" -m 644 "$1" "$PLUGIN_DIR/$DLL"
  fi
}

restore_backup() {  # $1 = backup folder; Jellyfin must be stopped
  if [ -f "$1/$DLL" ]; then install_dll "$1/$DLL"; else rm -f "$PLUGIN_DIR/$DLL"; fi
  [ -f "$1/config.xml" ] && cp "$1/config.xml" "$CONFIG_XML"
  cat "$1/version" > "$STATE_DIR/installed-version" 2>/dev/null || true
}

# ---------------------------------------------------------------------------------------------------------------------
if [ "$MODE" = "rollback" ]; then
  last="$(find "$STATE_DIR/backups" -mindepth 1 -maxdepth 1 -type d | sort | tail -n1)"
  [ -n "$last" ] || die "no backup in $STATE_DIR/backups"
  log "rolling back to backup $(basename "$last")"
  run "$SYSTEMCTL" stop "$JELLYFIN_SERVICE"
  [ "$DRY" = 1 ] || restore_backup "$last"
  run "$SYSTEMCTL" start "$JELLYFIN_SERVICE"
  [ "$DRY" = 1 ] || { wait_healthy && log "Jellyfin is healthy" || die "Jellyfin does not respond after the rollback"; }
  exit 0
fi

release="$(curl -fsS -m 30 -H 'Accept: application/vnd.github+json' "$API_URL/repos/$GITHUB_REPO/releases/latest")" \
  || die "cannot read the latest release of $GITHUB_REPO"
tag="$(jq -r '.tag_name // empty' <<<"$release")"
[[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "unexpected release tag: '${tag:-empty}'"
version="${tag#v}"

if [ "$FORCE" != 1 ]; then
  if [ "$(installed_version)" = "$version" ]; then log "up to date ($version)"; exit 0; fi
  if grep -qxF "$version" "$STATE_DIR/failed-versions" 2>/dev/null; then
    log "version $version already failed on this server: ignored (--force to retry)"; exit 0
  fi
fi

if [ "$MODE" = "check" ]; then
  log "update available: $(installed_version) -> $version (nothing installed)"; exit 0
fi

dll_url="$(jq -r --arg n "$DLL" '.assets[] | select(.name==$n) | .browser_download_url' <<<"$release" | head -n1)"
sums_url="$(jq -r '.assets[] | select(.name=="SHA256SUMS") | .browser_download_url' <<<"$release" | head -n1)"
[ -n "$dll_url" ] && [ -n "$sums_url" ] || die "release $tag does not contain $DLL and SHA256SUMS"

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
log "downloading $tag"
curl -fsSL -m 120 -o "$work/$DLL" "$dll_url" || die "cannot download the DLL"
curl -fsSL -m 30 -o "$work/SHA256SUMS" "$sums_url" || die "cannot download SHA256SUMS"
( cd "$work" && grep -F " $DLL" SHA256SUMS | sha256sum -c --status ) || die "wrong SHA-256 checksum: DLL rejected"
[ "$(head -c2 "$work/$DLL")" = "MZ" ] && [ "$(stat -c%s "$work/$DLL")" -gt 100000 ] || die "the downloaded file is not a plausible .NET assembly"

ts="$(date +%Y%m%d-%H%M%S)"
backup="$STATE_DIR/backups/${ts}_$(installed_version)"
log "backing up the current files into $backup"
if [ "$DRY" != 1 ]; then
  mkdir -p "$backup"
  [ -f "$PLUGIN_DIR/$DLL" ] && cp "$PLUGIN_DIR/$DLL" "$backup/$DLL"
  [ -f "$CONFIG_XML" ] && cp "$CONFIG_XML" "$backup/config.xml"
  installed_version > "$backup/version"
  mkdir -p "$PLUGIN_DIR"
fi

log "installing $version (stopping Jellyfin)"
run "$SYSTEMCTL" stop "$JELLYFIN_SERVICE"
[ "$DRY" = 1 ] || install_dll "$work/$DLL"
run "$SYSTEMCTL" start "$JELLYFIN_SERVICE"

if [ "$DRY" = 1 ]; then log "[dry run] done"; exit 0; fi

if wait_healthy; then
  echo "$version" > "$STATE_DIR/installed-version"
  log "OK: version $version installed, Jellyfin is healthy"
  # keep only the most recent backups
  find "$STATE_DIR/backups" -mindepth 1 -maxdepth 1 -type d | sort | head -n -"$KEEP_BACKUPS" | xargs -r rm -rf
  exit 0
fi

log "Jellyfin did not become healthy after installing $version: rolling back"
"$SYSTEMCTL" stop "$JELLYFIN_SERVICE"
restore_backup "$backup"
"$SYSTEMCTL" start "$JELLYFIN_SERVICE"
echo "$version" >> "$STATE_DIR/failed-versions"
wait_healthy && log "previous version restored, Jellyfin is healthy" || log "WARNING: Jellyfin still does not respond, check the journal"
exit 1
