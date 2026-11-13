# Automatic deployment from releases

The plugin is published with a **Git tag** and the server **fetches** the new version by itself. The server only makes outgoing requests to GitHub: no port to open, no access to grant, and no CI *runner* installed on your machine (not recommended for a public repository: an outside pull request could run code on it).

```
git tag v0.0.9 && git push --tags
        │
        ▼  GitHub Actions (cloud): builds, computes the SHA-256, publishes the release
        │      Jellyfin.Plugin.CustomMetadata.dll + SHA256SUMS
        ▼
Server: systemd timer (every 15 min) → custom-metadata-updater.sh
        downloads, checks the checksum, backs up, stops Jellyfin, installs, restarts,
        waits for "Healthy"; otherwise puts the previous version back
```

## Publishing a version

1. Update [`CHANGELOG.md`](../CHANGELOG.md) (new entry at the top).
2. `git tag v0.0.9 && git push origin v0.0.9`: the plugin version is the tag's (`-p:Version=0.0.9`). The tag must be spelled `vX.Y.Z`.
3. CI publishes the release. Builds without a tag (push, pull request) publish nothing: they are only a check.

## Installing the automatic update on the server

Requirements: `curl`, `jq`, `flock` (`util-linux` package), `systemd`. The script runs as root (it stops and restarts Jellyfin).

```bash
# from a clone of the repository, on the server
sudo install -m 755 deploy/custom-metadata-updater.sh /usr/local/bin/custom-metadata-updater.sh
sudo install -m 644 deploy/custom-metadata-updater.service deploy/custom-metadata-updater.timer /etc/systemd/system/
sudo install -m 600 deploy/custom-metadata-updater.env.example /etc/custom-metadata-updater.env
sudoedit /etc/custom-metadata-updater.env          # set GITHUB_REPO=owner/repository
sudo systemctl daemon-reload
sudo systemctl enable --now custom-metadata-updater.timer
```

Trying it by hand (first without installing anything, then for real):

```bash
sudo /usr/local/bin/custom-metadata-updater.sh --check      # says whether an update exists
sudo /usr/local/bin/custom-metadata-updater.sh --dry-run    # shows the steps
sudo /usr/local/bin/custom-metadata-updater.sh              # installs the latest release
journalctl -u custom-metadata-updater -n 50                 # what the timer did
```

## What the script checks and guarantees

- **The tag** must look like `vX.Y.Z`; the release must contain the DLL and `SHA256SUMS`.
- **The SHA-256 checksum** must match, and the file must look like a .NET assembly (`MZ`, over 100 KB).
- **Backup** before any replacement, in `/var/lib/custom-metadata-updater/backups/`: the DLL and the XML configuration (the 5 most recent are kept).
- **Health**: after the restart, Jellyfin's `/health` must answer `Healthy` (120 s at most). Otherwise the previous DLL is put back automatically, the version is recorded in `failed-versions` and is **not retried in a loop** (`--force` to retry it).
- Only one run at a time (lock).

Other commands: `--rollback` restores the latest backup; `NOTIFY_ONLY=1` in the environment file only reports updates in the journal, without installing them.

## Security to be aware of

Automatically installing a DLL published on GitHub means trusting the account that can publish a release: whoever controls it can make Jellyfin run code. Enable two-factor authentication on that account, protect the tags (`v*` tag protection rule) and, if you prefer to validate each version, set `NOTIFY_ONLY=1`. The SHA-256 check protects against a corrupted download, not against a malicious release.

## Testing the script without touching the server

```bash
deploy/test/test-updater.sh
```

It simulates GitHub, Jellyfin's `/health` and `systemctl` in a temporary folder and checks nine scenarios: first installation, already up to date, healthy update, a version that makes Jellyfin unhealthy (rollback), a failed version that is not retried, an invalid checksum, an unexpected tag, `--check` and `--rollback`.
