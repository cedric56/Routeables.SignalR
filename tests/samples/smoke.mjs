// Drives a web sample in Chrome against XAnimals.AngularServer, as a user would: the lists, the search, a detail, a dialog, the modal,
// a notification, the history and its region, the four regions, the tabs, the adoption, the lock of the menu, the theme, the back button
// and a reload. Every web sample draws the same classes (public/css), so the same test drives them all.
//
//   dotnet run --project samples/Angular/XAnimals.AngularServer          (http://localhost:5090)
//   npm --prefix samples/React/XAnimals.React run build && npm --prefix samples/React/XAnimals.React run preview
//   npm --prefix tests/samples ci && node tests/samples/smoke.mjs http://localhost:5174
//
// CHROME_PATH names the browser (google-chrome by default).
import puppeteer from 'puppeteer-core';

const base = process.argv[2];
if (!base) {
  console.error('Usage: node smoke.mjs <address of the app>');
  process.exit(2);
}
const browser = await puppeteer.launch({ executablePath: process.env.CHROME_PATH ?? '/usr/bin/google-chrome', headless: true, args: ['--no-sandbox'] });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', e => errors.push('pageerror: ' + e.message));
page.on('console', m => { if (m.type() === 'error' && !/Failed to load resource|ERR_/.test(m.text())) errors.push('console: ' + m.text()); });
const steps = [];
const step = async (name, fn) => {
  try { await fn(); steps.push('ok   ' + name); }
  catch (e) { steps.push('FAIL ' + name + ': ' + e.message.split('\n')[0]); }
};
const count = sel => page.$$eval(sel, els => els.length);
const until = async (fn, what, ms = 8000) => {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (await fn()) return; await new Promise(r => setTimeout(r, 50)); }
  throw new Error('timeout: ' + what);
};
const clickText = async (sel, text) => {
  const ok = await page.evaluate((sel, text) => {
    const el = [...document.querySelectorAll(sel)].find(e => e.textContent.trim().includes(text) && !e.disabled);
    if (!el) return false; el.click(); return true;
  }, sel, text);
  if (!ok) throw new Error(`no ${sel} "${text}"`);
};
const nav = async href => { await page.click(`a.drawer-link[href="${href}"]`); await until(async () => page.evaluate(h => location.pathname === h, href), 'route ' + href); };

await step('list of cats', async () => {
  await page.goto(base + '/cats', { waitUntil: 'domcontentloaded' });
  await until(async () => (await count('.animal-item')) > 3, 'animals');
  await until(async () => (await count('a.drawer-link')) > 5, 'menu');
});
await step('search', async () => {
  const before = await count('.animal-item');
  await page.type('.search-box', 'abys');
  await until(async () => { const n = await count('.animal-item'); return n > 0 && n < before; }, 'filtered');
});
await step('detail and back', async () => {
  await page.click('a.animal-item');
  await until(async () => (await count('.monkey-detail .title')) === 1, 'detail');
  await page.click('.back-btn');
  await until(async () => (await count('.animals-page')) === 1, 'list again');
});
await step('another list starts with its own search', async () => {
  await page.type('.search-box', 'x');   // typed in the list of cats
  await nav('/dogs');
  await until(async () => page.evaluate(() => document.querySelector('.animals-page h1')?.textContent === 'Dogs'), 'dogs');
  await until(async () => page.evaluate(() => document.querySelector('.search-box')?.value === ''), 'an empty search box');
  await until(async () => (await count('.animal-item')) > 3, 'dogs listed');
});
await step('a dialog, answered', async () => {
  await nav('/dialogs');
  await until(async () => (await count('.about-group button')) > 5, 'dialog buttons');
  await clickText('.about-group button', 'Yes / No');
  await until(async () => (await count('.web-dialog')) === 1, 'dialog shown');
  await clickText('.web-dialog button', 'No');
  await until(async () => (await count('.web-dialog')) === 0, 'dialog closed');
  await until(async () => (await count('.about-result')) === 1, 'result');
});
await step('the modal', async () => {
  await clickText('.about-group button', 'Show modal');
  await until(async () => (await count('.web-dialog input')) === 1, 'modal');
  await page.type('.web-dialog input', 'Hello');
  await until(async () => page.evaluate(() => [...document.querySelectorAll('.web-dialog button')].some(b => !b.disabled && /save/i.test(b.textContent))), 'save enabled');
  await clickText('.web-dialog button', 'Save');
  await until(async () => (await count('.web-dialog')) === 0, 'modal closed');
});
await step('a notification', async () => {
  await clickText('.about-group button', 'Information');
  await until(async () => (await count('.web-toast')) > 0, 'toast');
  await page.click('.web-toast-close');
  await until(async () => (await count('.web-toast')) === 0, 'toast closed');
});
await step('history and its region', async () => {
  await nav('/history');
  await until(async () => (await count('.nav-card')) > 0, 'cards');
  const before = await count('.nav-card');
  await clickText('.actions button', 'Push');
  await until(async () => (await count('.nav-card')) > before, 'pushed');
  await until(async () => page.evaluate(() => (document.querySelector('.nav-frame')?.textContent ?? '').trim().length > 0), 'region content');
});
await step('regions', async () => {
  await nav('/regions');
  await until(async () => (await count('.rg-app .rg-menu-item')) > 0 && (await count('.rg-header .rg-bar')) > 0, 'four regions');
  await page.click('.rg-menu-item:not(.current)');
  await until(async () => (await count('.rg-menu-item.current')) === 1, 'menu of a region');
});
await step('tabs (an address outside the menu)', async () => {
  await page.goto(base + '/tabs', { waitUntil: 'domcontentloaded' });
  await until(async () => page.evaluate(() => document.querySelector('.about-group h1')?.textContent === 'Cats'), 'first tab');
  await clickText('[role=tab]', 'Dogs');
  await until(async () => page.evaluate(() => document.querySelector('.about-group h1')?.textContent === 'Dogs'), 'second tab');
});
await step('adopt', async () => {
  await nav('/adopt');
  await until(async () => (await count('.adopt-choices .adopt-choice')) > 0, 'choices');
  await page.click('.adopt-choices .adopt-choice');
  await until(async () => (await count('.pet-card')) === 1, 'a pet');
});
await step('features: the lock of the menu', async () => {
  await nav('/features');
  await clickText('.about-group button', 'Lock');
  await until(async () => (await count('.drawer-link.locked')) > 0, 'locked');
  await clickText('.about-group button', 'Unlock');
  await until(async () => (await count('.drawer-link.locked')) === 0, 'unlocked');
});
await step('about', async () => {
  await nav('/about');
  await until(async () => (await count('.about-logo img')) === 1, 'about');
});
await step('theme', async () => {
  const before = await page.evaluate(() => document.documentElement.dataset.theme);
  await page.click('.theme-form');
  await until(async () => (await page.evaluate(() => document.documentElement.dataset.theme)) !== before, 'theme');
});
await step('the back button of the browser', async () => {
  await page.goBack();
  await until(async () => page.evaluate(() => location.pathname === '/features') && (await count('.drawer-link.active')) === 1, 'back');
});
await step('a reload on a detail address', async () => {
  await page.goto(base + '/dogs/Afghan%20Hound', { waitUntil: 'domcontentloaded' });
  await until(async () => (await count('.monkey-detail .title')) === 1, 'detail after reload');
});

await browser.close();
console.log(steps.join('\n'));
if (errors.length) console.log('errors:\n' + [...new Set(errors)].join('\n'));
const failed = steps.filter(s => s.startsWith('FAIL')).length + (errors.length ? 1 : 0);
console.log(failed ? `${failed} problem(s)` : 'all good');
process.exit(failed ? 1 : 0);
