// Quiet mode for the test scripts: preload it and only failures and summaries are printed.
//
//   node --import ./tools/ui-test/quiet.mjs tools/ui-test/config-local-ui.mjs <out_dir>
//
// Every script prints one "PASS <name>" line per check, and one "📸 <path>" line per screenshot.
// This hides both (they carry no information once the final "N PASS, M FAIL" summary says
// nothing failed; the screenshots stay on disk) and prints the number of hidden lines at exit.
// FAIL lines, summaries and every other line are untouched.
// Set QUIET=0 to disable without changing the command.

if (process.env.QUIET !== '0') {
  const original = console.log.bind(console);
  let hidden = 0;
  console.log = (...args) => {
    if (typeof args[0] === 'string' && /^(PASS\b|📸)/u.test(args[0])) { hidden++; return; }
    original(...args);
  };
  process.on('exit', () => { if (hidden) original(`(quiet: ${hidden} lines hidden)`); });
}
