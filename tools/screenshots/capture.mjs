// README screenshots + demo GIF of the Expo web build, captured with Playwright against `docker compose up`.
import { writeFileSync } from 'node:fs';
import gifenc from 'gifenc';
import { chromium } from 'playwright';
import { PNG } from 'pngjs';

const { GIFEncoder, applyPalette, quantize } = gifenc;

const WEB = process.env.RAF_WEB ?? 'http://localhost:58281';
const OUT = process.env.RAF_OUT ?? '/out';
const viewport = { width: 412, height: 915 };

const browser = await chromium.launch();
const context = await browser.newContext({ viewport, deviceScaleFactor: 2, locale: 'tr-TR', timezoneId: 'Europe/Istanbul' });
const page = await context.newPage();
const frames = [];

async function frame(holdMs = 1200) {
  // GIF frames at 1x to keep the file small; README stills at 2x.
  const png = PNG.sync.read(await page.screenshot({ scale: 'css' }));
  frames.push({ png, delay: holdMs });
}

async function expand(market) {
  const collapsed = page.getByRole('button', { name: new RegExp(`${market}.*, expand`) });
  if ((await collapsed.count()) > 0) await collapsed.click();
}

async function still(name) {
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(`saved ${name}.png`);
}

await page.goto(WEB, { waitUntil: 'networkidle' });
await page.getByText('Try the sample basket').waitFor();
await frame(1800);

await page.getByTestId('load-sample').click();
await page.getByTestId('summary').waitFor({ timeout: 20_000 });
await page.getByText('live prices').waitFor({ timeout: 20_000 });
await page.waitForTimeout(800);
await still('01-compare');
await frame(2500);

// Tazelik: several prices untouched for days → stale share warning and red "as of" badges.
await page.getByTestId('market-tazelik').scrollIntoViewIfNeeded();
await expand('Tazelik');
await page.getByTestId('stale-warning-tazelik').waitFor();
await page.getByTestId('stale-warning-tazelik').scrollIntoViewIfNeeded();
await page.waitForTimeout(400);
await still('02-stale-prices');
await frame(2500);

// Çarşı: cheapest per item, but a missing item makes its total incomplete (ranked last, struck through).
await page.getByTestId('market-carsi').scrollIntoViewIfNeeded();
await expand('Çarşı');
await page.waitForTimeout(400);
await page.getByTestId('market-carsi').scrollIntoViewIfNeeded();
await still('03-incomplete-market');
await frame(2200);

// Offline: the saved comparison stays, with its age.
await page.getByTestId('summary').scrollIntoViewIfNeeded();
await context.setOffline(true);
await page.getByTestId('banner-offline').waitFor({ timeout: 10_000 });
await page.waitForTimeout(400);
await still('04-offline');
await frame(2500);
await context.setOffline(false);
await page.waitForTimeout(1500);

// Restart with the server unreachable: the persisted cache renders the last result and says so.
// Wait past the 30 s staleTime first, so the restarted app actually tries (and fails) to refresh it.
await page.waitForTimeout(31_000);
await context.route('**/api/**', (route) => route.abort('connectionrefused'));
await context.route('**/hubs/**', (route) => route.abort('connectionrefused'));
await page.reload({ waitUntil: 'domcontentloaded' });
await page.getByTestId('tab-compare').click();
await page.getByTestId('banner-unreachable').waitFor({ timeout: 30_000 }).catch(async (e) => {
  await page.screenshot({ path: `${OUT}/debug-unreachable.png` });
  throw e;
});
await page.waitForTimeout(400);
await still('05-unreachable-after-restart');
await frame(2800);
await context.unrouteAll({ behavior: 'ignoreErrors' });

// Basket tab.
await page.reload({ waitUntil: 'networkidle' });
await page.getByTestId('tab-basket').click();
await page.waitForTimeout(800);
await still('06-basket');
await frame(1800);

await browser.close();

// Encode the GIF (256-colour palette per frame).
const gif = GIFEncoder();
for (const { png, delay } of frames) {
  const rgba = new Uint8Array(png.data.buffer, png.data.byteOffset, png.data.length);
  const palette = quantize(rgba, 256);
  gif.writeFrame(applyPalette(rgba, palette), png.width, png.height, { palette, delay });
}
gif.finish();
writeFileSync(`${OUT}/raf-demo.gif`, gif.bytes());
console.log(`saved raf-demo.gif (${frames.length} frames)`);
