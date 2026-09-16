#!/usr/bin/env node
/**
 * Registers a webhook with eDrafter and prints the signing secret.
 *
 * eDrafter shows the secret EXACTLY ONCE, at creation. If you lose it you cannot
 * retrieve it — you have to delete the webhook and register a new one. So the script
 * prints it loudly and tells you where to put it.
 *
 * Usage:
 *   node register-webhook.js --list
 *   node register-webhook.js --url https://your-app.com/webhooks/edrafter
 *   node register-webhook.js --delete <webhook-id>
 *
 * Against the mock (default), pass nothing — it reads sensible defaults.
 * Against the real API:
 *   node register-webhook.js --base https://edrafterb2b.in/api/v1 --key <API_KEY> \
 *                            --url https://your-public-host/webhooks/edrafter
 *
 * Node 18+ only (uses the built-in fetch). No npm install needed.
 */

const args = process.argv.slice(2);

function arg(name, fallback = null) {
  const i = args.indexOf(`--${name}`);
  return i !== -1 && args[i + 1] ? args[i + 1] : fallback;
}
const has = (name) => args.includes(`--${name}`);

const BASE = (arg('base', 'http://localhost:5099/api/v1')).replace(/\/$/, '');
const KEY = arg('key', process.env.EDRAFTER_API_KEY || 'mock-key');
const URL_ = arg('url', 'http://localhost:5100/webhooks/edrafter');

// These are the only two events eDrafter publishes. There is nothing for order
// failure, e-sign decline, e-sign expiry, or individual signatures — those are
// only visible by polling GET /orders/:idd and GET /esign/:id.
const EVENTS = ['order.completed', 'esign.completed'];

const headers = { 'x-api-key': KEY, 'Content-Type': 'application/json' };

async function call(method, path, body) {
  const res = await fetch(`${BASE}${path}`, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let parsed;
  try { parsed = JSON.parse(text); } catch { parsed = { raw: text }; }
  return { status: res.status, ok: res.ok, body: parsed };
}

async function list() {
  const res = await call('GET', '/webhooks');
  if (!res.ok) {
    console.error(`\n  Failed (HTTP ${res.status}):`, res.body);
    process.exit(1);
  }
  const hooks = res.body.webhooks ?? [];
  console.log(`\n  ${hooks.length} webhook(s) registered at ${BASE}\n`);
  for (const h of hooks) {
    console.log(`    id     : ${h._id}`);
    console.log(`    url    : ${h.url}`);
    console.log(`    events : ${(h.events ?? []).join(', ')}`);
    console.log(`    active : ${h.active}\n`);
  }
  if (res.body.supportedEvents) {
    console.log(`  Supported events: ${res.body.supportedEvents.join(', ')}\n`);
  }
}

async function register() {
  console.log(`\n  Registering webhook`);
  console.log(`    api    : ${BASE}`);
  console.log(`    url    : ${URL_}`);
  console.log(`    events : ${EVENTS.join(', ')}\n`);

  if (URL_.includes('localhost') && !BASE.includes('localhost')) {
    console.log('  !! You are registering a LOCALHOST url with a REMOTE api.');
    console.log('     eDrafter cannot reach your machine — use a public https url');
    console.log('     (ngrok or a dev tunnel) or the deployed host.\n');
  }

  const res = await call('POST', '/webhooks', {
    url: URL_,
    events: EVENTS,
    description: 'eDrafter POC listener',
  });

  if (!res.ok) {
    console.error(`  Failed (HTTP ${res.status}):`, res.body);
    process.exit(1);
  }

  const secret = res.body.webhook?.secret;
  const id = res.body.webhook?._id;

  console.log('  Registered.\n');
  console.log(`    webhook id : ${id}`);
  console.log(`    secret     : ${secret}\n`);
  console.log('  ┌──────────────────────────────────────────────────────────────┐');
  console.log('  │  SAVE THE SECRET NOW. eDrafter shows it only once.           │');
  console.log('  │  Without it you cannot verify that a delivery is genuine.    │');
  console.log('  └──────────────────────────────────────────────────────────────┘\n');
  console.log('  Give it to the backend when you start it:\n');
  console.log(`    PowerShell   $env:EDrafter__WebhookSecret="${secret}"`);
  console.log(`    Git Bash     export EDrafter__WebhookSecret="${secret}"\n`);
  console.log('    then:        dotnet run --project EDrafter.Api --launch-profile api\n');
  console.log('  In production this belongs in Key Vault, never in a committed file.\n');
}

async function remove(id) {
  const res = await call('DELETE', `/webhooks/${id}`);
  console.log(res.ok ? `\n  Deleted ${id}\n` : `\n  Failed (HTTP ${res.status}): ${JSON.stringify(res.body)}\n`);
}

(async () => {
  try {
    if (has('list')) return await list();
    const del = arg('delete');
    if (del) return await remove(del);
    await register();
  } catch (err) {
    console.error('\n  Could not reach the API:', err.message);
    console.error('  Is it running, and is --base correct?\n');
    process.exit(1);
  }
})();
