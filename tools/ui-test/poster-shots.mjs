// Screenshots of a landscape series poster in the Jellyfin series grid, option "Recadrer les posters paysage en portrait 2:3" off then on,
// and of the "Miniatures et posters" section of the source editor. Restores the poster image and the configuration.
// Usage: node poster-shots.mjs <base_url> <api_img_dir> <out_dir>
import { createRequire } from 'node:module';
import fs from 'node:fs';
import { execFileSync } from 'node:child_process';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, IMGDIR, OUT] = process.argv.slice(2);
if (!J || !IMGDIR || !OUT) { console.error('usage: node poster-shots.mjs <base_url> <api_img_dir> <out_dir>'); process.exit(2); }
fs.mkdirSync(OUT, { recursive: true });
const P = '12345678-1234-1234-1234-123456789abc';
const IMG = IMGDIR + '/aventuriers.jpg';
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-posterp", Version="1.0"' }, body: JSON.stringify({ Username: 'admin', Pw: 'admin' }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const api = async (m, u, b) => { const r = await fetch(J + u, { method: m, headers: H, body: b === undefined ? undefined : JSON.stringify(b) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const wait = ms => new Promise(r => setTimeout(r, ms));
const original = await api('GET', `/Plugins/${P}/Configuration`);
const series = (await api('GET', '/Items?IncludeItemTypes=Series&Recursive=true')).Items.find(i => i.Name.includes('Aventuriers'));
const lib = (await api('GET', '/Library/VirtualFolders'))[0].ItemId;
const refresh = async () => { await api('POST', `/Items/${series.Id}/Refresh?Recursive=false&ImageRefreshMode=FullRefresh&MetadataRefreshMode=FullRefresh&ReplaceAllImages=true&ReplaceAllMetadata=false`); await wait(8000); };
const login = async page => {
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', 'admin');
  await page.fill('#txtManualPassword', 'admin');
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
};
fs.copyFileSync(IMG, IMG + '.orig');
const browser = await chromium.launch({ channel: 'chrome' });
try {
  execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=1920x1080:d=1', '-frames:v', '1', IMG]);
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 700 }, deviceScaleFactor: 2 })).newPage();
  await login(page);
  for (const on of [false, true]) {
    const config = JSON.parse(JSON.stringify(original));
    config.Sources.find(s => s.Prefix === 'dm').LandscapePosterCrop = on;
    await api('POST', `/Plugins/${P}/Configuration`, config);
    await refresh();
    await page.goto('about:blank');
    await page.goto(`${J}/web/#/tv?topParentId=${lib}&collectionType=tvshows`);
    await page.waitForTimeout(4000);
    for (const card of await page.$$('.card')) {
      if ((await card.textContent()).includes('Aventuriers')) { await card.screenshot({ path: `${OUT}/poster-card-${on ? 'on' : 'off'}.png` }); break; }
    }
  }
  const page2 = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  await login(page2);
  await page2.goto(`${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider`);
  await page2.waitForSelector('#CustomMetadataConfigPage .cmn-nav', { state: 'visible' });
  await page2.waitForSelector('#CustomMetadataConfigPage #sourcesList .cm-card');
  await page2.click('#CustomMetadataConfigPage .cm-card button.raised');
  await page2.waitForTimeout(1500);
  await page2.click('#CustomMetadataConfigPage [data-go=thumbs]');
  await page2.waitForTimeout(800);
  await page2.screenshot({ path: `${OUT}/poster-config.png` });
} finally {
  fs.renameSync(IMG + '.orig', IMG);
  await api('POST', `/Plugins/${P}/Configuration`, original);
  await refresh();
  await browser.close();
}
