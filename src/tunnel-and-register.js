#!/usr/bin/env node
/**
 * Opens a public tunnel to the local backend and registers that url as an eDrafter
 * webhook, then prints the signing secret and stays open.
 *
 * Why a tunnel is needed: eDrafter's servers deliver webhooks by making an HTTP request
 * TO you. They cannot reach http://localhost — that address means "my own machine" on
 * their server too. A tunnel gives your machine a temporary public https address.
 *
 *   eDrafter  ->  https://xyz.loca.lt  ->  your machine  ->  localhost:5100
 *
 * Usage:
 *   node tunnel-and-register.js --key YOUR_REAL_API_KEY
 *   node tunnel-and-register.js --key YOUR_KEY --provider ngrok
 *   node tunnel-and-register.js --dry-run              (tunnel only, no registration)
 *
 * Providers:
 *   localtunnel (default)  npm i -D localtunnel      no signup
 *   ngrok                  npm i -D @ngrok/ngrok     needs a free authtoken
 *
 * Leave this running while you test. Closing it drops the tunnel, and eDrafter's
 * deliveries will start failing.
 */

const args = process.argv.slice(2);
const arg = (n, d = null) => {
  const i = args.indexOf(`--${n}`);
  return i !== -1 && args[i + 1] ? args[i + 1] : d;
};
const has = (n) => args.includes(`--${n}`);

const PROVIDER = arg('provider', 'localtunnel');
const LOCAL_PORT = Number(arg('port', 5100));
const BASE = arg('base', 'https://edrafterb2b.in/api/v1').replace(/\/$/, '');
const KEY = arg('key', process.env.EDRAFTER_API_KEY);
const DRY = has('dry-run');

const EVENTS = ['order.completed', 'esign.completed'];

if (!KEY && !DRY) {
  console.error('\n  Need an eDrafter API key.\n');
  console.error('    node tunnel-and-register.js --key YOUR_KEY');
  console.error('    (or set EDRAFTER_API_KEY, or pass --dry-run to just open the tunnel)\n');
  process.exit(1);
}

async function openTunnel() {
  if (PROVIDER === 'ngrok') {
    let ngrok;
    try {
      ngrok = require('@ngrok/ngrok');
    } catch {
      console.error('\n  @ngrok/ngrok is not installed.\n');
      console.error('    npm install -D @ngrok/ngrok');
      console.error('    then set NGROK_AUTHTOKEN (free at dashboard.ngrok.com)\n');
      process.exit(1);
    }
    if (!process.env.NGROK_AUTHTOKEN) {
      console.error('\n  NGROK_AUTHTOKEN is not set. Get a free one at dashboard.ngrok.com\n');
      console.error('    PowerShell  $env:NGROK_AUTHTOKEN="..."');
      console.error('    Git Bash    export NGROK_AUTHTOKEN="..."\n');
      process.exit(1);
    }
    const listener = await ngrok.forward({ addr: LOCAL_PORT, authtoken_from_env: true });
    return { url: listener.url(), close: () => listener.close() };
  }

  let localtunnel;
  try {
    localtunnel = require('localtunnel');
  } catch {
    console.error('\n  localtunnel is not installed.\n');
    console.error('    npm install -D localtunnel\n');
    process.exit(1);
  }
  const t = await localtunnel({ port: LOCAL_PORT });
  return { url: t.url, close: () => t.close() };
}

async function registerWebhook(publicUrl) {
  const callback = `${publicUrl}/webhooks/edrafter`;

  const res = await fetch(`${BASE}/webhooks`, {
    method: 'POST',
    headers: { 'x-api-key': KEY, 'Content-Type': 'application/json' },
    body: JSON.stringify({ url: callback, events: EVENTS, description: 'eDrafter POC (tunnel)' }),
  });

  const text = await res.text();
  let body;
  try { body = JSON.parse(text); } catch { body = { raw: text }; }

  if (!res.ok) {
    console.error(`\n  Registration failed (HTTP ${res.status}):`, body);
    if (res.status === 403) {
      console.error('\n  403 means the key is invalid or disabled. Note that the key which was');
      console.error('  circulated in a Word document was flagged for rotation — if you are using');
      console.error('  that one, ask eDrafter for a fresh key.\n');
    }
    return null;
  }

  return { id: body.webhook?._id, secret: body.webhook?.secret, callback };
}

(async () => {
  console.log(`\n  Opening a ${PROVIDER} tunnel to localhost:${LOCAL_PORT} ...`);

  let tunnel;
  try {
    tunnel = await openTunnel();
  } catch (err) {
    console.error(`\n  Could not open the tunnel: ${err.message}\n`);
    process.exit(1);
  }

  console.log(`\n  Public url : ${tunnel.url}`);
  console.log(`  Forwarding : ${tunnel.url}  ->  http://localhost:${LOCAL_PORT}\n`);

  if (DRY) {
    console.log('  --dry-run: tunnel only, nothing registered.');
    console.log(`  Your webhook endpoint would be: ${tunnel.url}/webhooks/edrafter\n`);
    console.log('  Leave this running. Ctrl-C to close.\n');
  } else {
    console.log(`  Registering with ${BASE} ...`);
    const result = await registerWebhook(tunnel.url);

    if (result) {
      console.log('\n  Registered.\n');
      console.log(`    webhook id : ${result.id}`);
      console.log(`    callback   : ${result.callback}`);
      console.log(`    secret     : ${result.secret}\n`);
      console.log('  ┌──────────────────────────────────────────────────────────────┐');
      console.log('  │  SAVE THE SECRET. eDrafter shows it only once.               │');
      console.log('  └──────────────────────────────────────────────────────────────┘\n');
      console.log('  Restart the backend with it, in another terminal:\n');
      console.log(`    PowerShell   $env:EDrafter__WebhookSecret="${result.secret}"`);
      console.log(`    Git Bash     export EDrafter__WebhookSecret="${result.secret}"`);
      console.log('    then         dotnet run --project EDrafter.Api --launch-profile api\n');
      console.log('  Also set Webhooks:AutoRegister to false so the backend does not');
      console.log('  register a second localhost hook over the top of this one.\n');
    }

    console.log('  Leave this running while you test. Ctrl-C closes the tunnel.\n');
  }

  const shutdown = async () => {
    console.log('\n  Closing the tunnel. eDrafter can no longer reach you.');
    if (!DRY) console.log('  Delete the webhook when you are done:');
    if (!DRY) console.log('    node register-webhook.js --base ' + BASE + ' --key <KEY> --delete <id>\n');
    try { await tunnel.close(); } catch {}
    process.exit(0);
  };

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
  setInterval(() => {}, 1 << 30); // hold the process open
})();
