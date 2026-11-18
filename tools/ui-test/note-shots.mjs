// Screenshots of the local editor tags/genres pill editor and the private note in the REAL jellyfin-web.
// Usage: node note-shots.mjs <base_url> <out_dir> [user] [password]. Seeds a local source, restores the config at the end.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
const require = createRequire(import.meta.url);
const { chromium } = require('playwright');
const [J, outDir, user = 'admin', password = 'admin'] = process.argv.slice(2);
fs.mkdirSync(outDir, { recursive: true });
const PLUGIN = '12345678-1234-1234-1234-123456789abc', DATA = 'noteshots000001', C = '#CustomMetadataConfigPage';
const auth = await (await fetch(`${J}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="t", Device="cli", DeviceId="cm-note", Version="1.0"' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
const H = { 'Content-Type': 'application/json', Authorization: `MediaBrowser Token="${auth.AccessToken}"` };
const rest = async (m, u, b) => { const r = await fetch(J + u, { method: m, headers: H, body: b === undefined ? undefined : JSON.stringify(b) }); const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const original = await rest('GET', `/Plugins/${PLUGIN}/Configuration`);
const browser = await chromium.launch({ channel: 'chrome' });
try {
  const cfg = JSON.parse(JSON.stringify(original));
  cfg.Sources = cfg.Sources.filter(s => s.LocalDataId !== DATA);
  const tmpl = cfg.Sources.find(s => s.SourceType !== 'Local') || {};
  cfg.Sources.push({ ...tmpl, Name: 'Note test', Prefix: 'nt', Enabled: true, SourceType: 'Local', LocalDataId: DATA, BaseUrl: 'local://local', VideoEndpoint: '/videos/{id}', SourceEndpoint: '/sources/{id}', SeriesEndpoint: '/series/{id}', EpisodeListEndpoint: '/sources/{id}/videos' });
  await rest('PUT', `/CustomMetadata/Local/${DATA}`, { Version: 1, Series: [{ Id: 'ser1', Title: 'Ma série', Tags: ['voyage', 'nature'], Genres: ['Documentaire'], Note: 'À vérifier : le poster vient du site officiel.', Seasons: [{ Id: 'sa1', Title: 'Saison 1', Episodes: [{ Id: 'ep1', Title: 'Pilote', IndexNumber: 1 }] }] }] });
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, cfg);
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 1100 } })).newPage();
  const shot = async n => { const f = path.join(outDir, n + '.png'); await page.screenshot({ path: f }); console.log('📸', f); };
  await page.goto(`${J}/web/#/login`);
  await page.fill('#txtManualName', user); await page.fill('#txtManualPassword', password);
  await page.click('.manualLoginForm button[type=submit]');
  await page.waitForFunction(() => !location.hash.includes('login'));
  await page.goto(`${J}/web/#/configurationpage?name=Custom%20Metadata%20Provider`);
  await page.waitForSelector(`${C} .cm-card`, { timeout: 30000 }); await page.waitForTimeout(1000);
  await page.evaluate(c => { Array.from(document.querySelectorAll(c + ' .cm-card')).find(x => x.textContent.includes('Note test')).querySelector('.raised').click(); }, C);
  await page.click(`${C} [data-go="content"]`); await page.waitForTimeout(500);
  await page.click(`${C} #localTree [data-node="s:ser1"]`); await page.waitForTimeout(800);
  await page.locator(`${C} #lf-Tags`).scrollIntoViewIfNeeded();
  await page.click(`${C} #lf-Tags`); await page.keyboard.type('na'); await page.waitForTimeout(500);
  await shot('N1-autocomplete');
  await page.keyboard.type('xyz'); await page.waitForTimeout(300);
  await shot('N2-new-term');
  await page.keyboard.press('Enter'); await page.waitForTimeout(300);
  await page.keyboard.press('Escape'); await page.click(`${C} #lf-Genres`); await page.waitForTimeout(400);
  await page.locator(`${C} #lf-Note`).scrollIntoViewIfNeeded(); await page.waitForTimeout(300);
  await shot('N3-genres-note');
  await page.setViewportSize({ width: 412, height: 900 }); await page.waitForTimeout(500);
  await page.locator(`${C} #lf-Tags`).scrollIntoViewIfNeeded(); await shot('N4-mobile');
} finally {
  await rest('POST', `/Plugins/${PLUGIN}/Configuration`, original);
  await rest('DELETE', `/CustomMetadata/Local/${DATA}`);
  await browser.close();
}
