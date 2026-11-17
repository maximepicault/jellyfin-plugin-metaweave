// Keeps the headless Chrome of a test script from outliving it.
//
// A script that spawns Chrome must start it with `detached: true` (its own process group) and call trackChrome(chrome, profile)
// right after. The whole group (Chrome and its renderer / GPU / crashpad children) is killed and the profile folder removed when
// the script ends, whatever the way (Chrome's own temporary folders are redirected with TMPDIR, see chromeTmp): end of the run, an uncaught error, Ctrl-C, or a SIGTERM from `timeout`. A plain
// `chrome.kill()` only reaches the main process and never runs when the script itself is killed: that left hundreds of
// profiles (60 MB each) and Chrome processes behind in /tmp and the cache folder.
import fs from 'node:fs';

// Short temporary folder given to Chrome as TMPDIR: it puts its own scoped folders (and a Unix socket, whose path is limited to
// about 108 characters, so the profile's long path would not do) there. Removed with the profile when the script ends.
const temps = [];
export function chromeTmp() {
  const dir = fs.mkdtempSync('/tmp/ct-');
  temps.push(dir);
  return dir;
}

export function trackChrome(chrome, profile) {
  let stopped = false;
  const stop = () => {
    if (stopped) { return; }
    stopped = true;
    // Politely first (Chrome tidies its own files), then for good
    try { process.kill(-chrome.pid, 'SIGTERM'); Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 600); } catch { /* already gone */ }
    try { process.kill(-chrome.pid, 'SIGKILL'); } catch { try { chrome.kill('SIGKILL'); } catch { /* already gone */ } }
    for (const dir of [profile, ...temps]) {
      try { fs.rmSync(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch { /* the folder may still be flushed: harmless */ }
    }
  };
  process.on('exit', stop);
  for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
    process.on(signal, () => { stop(); process.exit(130); });
  }
  process.on('uncaughtException', error => { console.error(error); stop(); process.exit(1); });
  return stop;
}
