// Drives the real UI the way a user would: fill the form, walk both spend gates,
// and watch the status change. Screenshots each step so the render can be inspected.
const { chromium } = require('playwright');
const path = require('path');

const OUT = process.env.SHOT_DIR;
const MOCK = 'http://localhost:5099';

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 1100 } });

  const errors = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push('PAGEERROR: ' + e.message));

  const step = async (name) => {
    await page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: true });
    console.log(`   shot: ${name}.png`);
  };

  console.log('1. Load the app');
  await page.goto('http://localhost:4200', { waitUntil: 'networkidle' });
  await page.waitForTimeout(1200);

  const h1 = await page.textContent('h1').catch(() => null);
  console.log(`   title: ${h1}`);

  // The chips prove the UI actually reached the API, not just rendered static markup.
  const chips = await page.$$eval('.chip', els => els.map(e => e.textContent.trim()));
  console.log(`   chips: ${JSON.stringify(chips)}`);

  const formVisible = await page.isVisible('form');
  console.log(`   form rendered: ${formVisible}`);
  await step('01-form');

  console.log('2. Fill the lease form');
  await page.fill('input[formControlName="firstPartyName"]', 'Ramesh Kumar');
  await page.fill('input[formControlName="firstPartyEmail"]', 'ramesh@example.com');
  await page.fill('input[formControlName="firstPartyPhone"]', '9876543210');
  await page.fill('input[formControlName="secondPartyName"]', 'Jane Smith');
  await page.fill('input[formControlName="secondPartyEmail"]', 'jane@example.com');
  await page.fill('input[formControlName="secondPartyPhone"]', '9876543211');
  await page.fill('textarea[formControlName="propertyAddress"]',
                  'No 42, 3rd Cross, Indiranagar, Bengaluru 560038');
  await page.fill('input[formControlName="considerationAmount"]', '60000');
  await page.fill('input[formControlName="monthlyRent"]', '20000');
  await page.waitForTimeout(400);

  const duty = await page.textContent('.duty-value');
  console.log(`   live duty preview at 60,000 consideration: ${duty}   (expect Rs.300)`);
  await step('02-form-filled');

  console.log('3. Continue to review');
  await page.click('button[type="submit"]');
  await page.waitForSelector('.costs', { timeout: 15000 });
  await page.waitForTimeout(600);

  const rows = await page.$$eval('.costs tr', trs =>
    trs.map(tr => tr.innerText.replace(/\s+/g, ' ').trim()).filter(Boolean));
  console.log('   cost breakdown shown before any spend:');
  rows.forEach(r => console.log(`     ${r}`));
  await step('03-review');

  console.log('4. The spend gate blocks until confirmed');
  const gateBtn = page.locator('.spend-gate button.spend');
  console.log(`   spend button disabled before confirming: ${await gateBtn.isDisabled()}`);
  console.log(`   button label: ${(await gateBtn.textContent()).trim()}`);

  await page.click('.spend-gate .check input');
  await page.waitForTimeout(300);
  console.log(`   disabled after confirming: ${await gateBtn.isDisabled()}`);
  await step('04-gate-armed');

  console.log('5. Place the order (spend 1)');
  await gateBtn.click();
  await page.waitForSelector('.timeline', { timeout: 20000 });
  await page.waitForTimeout(800);
  const status1 = (await page.textContent('.track-head .status')).trim();
  console.log(`   status: ${status1}`);
  await step('05-ordered');

  console.log('6. Complete the order at eDrafter -> webhook -> live update');
  const list = await (await fetch(`${MOCK}/api/v1/orders`, {
    headers: { 'x-api-key': 'k' }
  })).json();
  const newest = list.orders[0];
  console.log(`   forcing order ${newest._idd} to Completed`);
  await fetch(`${MOCK}/__mock/orders/${newest._idd}/complete`, { method: 'POST' });

  // No reload: SignalR should push the change into the open page.
  await page.waitForFunction(
    () => document.querySelector('.track-head .status')?.textContent.trim() === 'StampReady',
    { timeout: 25000 }
  ).catch(() => console.log('   !! live update did not arrive in time'));
  await page.waitForTimeout(700);

  const status2 = (await page.textContent('.track-head .status')).trim();
  console.log(`   status after webhook (no page reload): ${status2}`);
  const banner = await page.textContent('.banner.info').catch(() => null);
  if (banner) console.log(`   live banner: ${banner.replace('dismiss', '').trim()}`);
  await step('06-stamp-ready');

  console.log('7. The second spend gate appears, with the non-refundable warning');
  const warn = await page.textContent('.spend-gate.danger .warn').catch(() => null);
  console.log(`   warning shown: ${warn ? warn.trim() : 'NOT FOUND'}`);
  const sendBtn = page.locator('.spend-gate.danger button.spend');
  console.log(`   send disabled before confirming: ${await sendBtn.isDisabled()}`);
  await step('07-esign-gate');

  console.log('8. Send for signing (spend 2)');
  await page.click('.spend-gate.danger .check input');
  await page.waitForTimeout(250);
  await sendBtn.click();
  await page.waitForTimeout(2500);
  const status3 = (await page.textContent('.track-head .status')).trim();
  console.log(`   status: ${status3}`);
  const sigs = await page.$$eval('.sig', els =>
    els.map(e => e.innerText.replace(/\s+/g, ' ').trim()));
  sigs.forEach(s => console.log(`     ${s}`));
  await step('08-sent');

  console.log('9. Sign everything -> esign.completed webhook');
  const docs = await (await fetch(`${MOCK}/api/v1/esign`, {
    headers: { 'x-api-key': 'k' }
  })).json();
  const doc = docs.documents[0];
  await fetch(`${MOCK}/__mock/esign/${doc.documentId}/sign-all`, { method: 'POST' });

  await page.waitForFunction(
    () => document.querySelector('.track-head .status')?.textContent.trim() === 'Signed',
    { timeout: 25000 }
  ).catch(() => console.log('   !! signed update did not arrive'));
  await page.waitForTimeout(700);

  const status4 = (await page.textContent('.track-head .status')).trim();
  console.log(`   final status: ${status4}`);
  const hasDownload = await page.isVisible('a[href*="signed-pdf"]');
  console.log(`   signed PDF download offered: ${hasDownload}`);
  await step('09-signed');

  console.log('\nconsole errors: ' + (errors.length ? errors.join(' | ') : 'none'));
  await browser.close();
})();
