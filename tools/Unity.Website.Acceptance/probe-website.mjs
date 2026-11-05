// Isolated loopback website UI acceptance. Never uses a person's browser profile or credentials.
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';

const [browser, origin, output] = process.argv.slice(2);
if (!browser || !origin || !output || !['127.0.0.1', 'localhost'].includes(new URL(origin).hostname)) throw new Error('Supply an installed Chromium executable, loopback website origin and fresh owned output directory.');
await fs.mkdir(output, { recursive: true });
const profile = path.join(output, 'browser-profile');
await fs.mkdir(profile, { recursive: false });
const child = spawn(browser, ['--headless=new', '--no-first-run', '--disable-default-apps', '--disable-extensions', '--disable-save-password-bubble', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore', windowsHide: true });
const pending = new Map(), checks = [], failures = [];
let socket, sequence = 0, sessionId;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function command(method, params = {}, attached = sessionId) {
  return new Promise((resolve, reject) => { const id = ++sequence; pending.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params, ...(attached ? { sessionId: attached } : {}) })); });
}
async function evaluate(expression) {
  const value = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (value.exceptionDetails) throw new Error('Website action raised an exception.');
  return value.result?.value;
}
async function until(label, expression) {
  for (let i = 0; i < 100; i++) { if (await evaluate(expression)) { checks.push(label); return; } await delay(100); }
  throw new Error(`Website did not complete: ${label}`);
}
async function screenshot(name) {
  await evaluate('document.activeElement?.blur(); document.documentElement.scrollTop = 0; document.body.scrollTop = 0; window.scrollTo(0, 0)'); await delay(300);
  if (await evaluate("document.querySelector('.topbar').getBoundingClientRect().top < -1")) throw new Error('Website header is clipped before capture.');
  const value = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  await fs.writeFile(path.join(output, name + '.png'), Buffer.from(value.data, 'base64'));
}
let result = { result: 'FAIL' };
try {
  let endpoint;
  for (let i = 0; i < 100; i++) { try { const parts = (await fs.readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).trim().split(/\r?\n/); endpoint = `ws://127.0.0.1:${parts[0]}${parts[1]}`; break; } catch { await delay(100); } }
  if (!endpoint) throw new Error('Owned browser did not open its debugging endpoint.');
  socket = new WebSocket(endpoint); await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    if (message.id && pending.has(message.id)) { const waiter = pending.get(message.id); pending.delete(message.id); message.error ? waiter.reject(new Error('Browser protocol action failed.')) : waiter.resolve(message.result); }
    else if (message.method === 'Runtime.exceptionThrown') failures.push('Uncaught website JavaScript exception');
  };
  const target = await command('Target.createTarget', { url: 'about:blank' }, null);
  ({ sessionId } = await command('Target.attachToTarget', { targetId: target.targetId, flatten: true }, null));
  await command('Runtime.enable'); await command('Page.enable');
  await command('Emulation.setDeviceMetricsOverride', { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
  await command('Page.navigate', { url: origin });
  await until('guest page loads live status', "document.getElementById('connection-status')?.textContent !== 'Connecting' && document.getElementById('channel-title')?.textContent === 'Global'");
  await screenshot('01-guest');
  const username = 'ui' + Date.now(), password = randomBytes(24).toString('base64url') + '!aA9';
  await evaluate(`document.getElementById('account-button').click(); document.querySelector('[data-account-mode="register"]').click(); document.getElementById('username').value=${JSON.stringify(username)}; document.getElementById('password').value=${JSON.stringify(password)}; document.getElementById('display-name').value='UI Explorer'; document.getElementById('account-form').requestSubmit();`);
  await until('registration and live account socket', "document.getElementById('account-button').textContent.includes('UI Explorer') && !document.getElementById('chat-text').disabled && document.getElementById('connection-status').textContent.toLowerCase().includes('live')");
  await evaluate("document.getElementById('chat-text').value='Hello from the second-release UI check'; document.getElementById('chat-form').requestSubmit();");
  await until('chat send is rendered from server', "document.getElementById('messages').textContent.includes('Hello from the second-release UI check')");
  await screenshot('02-live-chat');
  await evaluate("location.hash='communities'; document.getElementById('create-community').click();");
  await until('community creation form opens', "document.getElementById('action-dialog').open && document.querySelector('#action-fields [name=name]')");
  const communityName = 'SocketJack Explorers ' + username.slice(-5);
  await evaluate(`document.querySelector('#action-fields [name=name]').value=${JSON.stringify(communityName)}; document.querySelector('#action-fields [name=description]').value='Created through the real website UI'; document.getElementById('action-form').requestSubmit();`);
  await until('community creation persists and displays', `!document.getElementById('action-dialog').open && document.getElementById('community-list').textContent.includes(${JSON.stringify(communityName)})`);
  await screenshot('03-community');
  await evaluate("location.hash='groups'; document.getElementById('create-party').click();");
  await until('party creation displays membership', "document.getElementById('group-detail').textContent.includes('UI Explorer')");
  await screenshot('04-party');
  await evaluate("location.hash='status'; document.getElementById('refresh-status').click();");
  await until('status page renders metrics', "document.getElementById('server-status').children.length >= 4");
  await screenshot('05-status');
  await command('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await evaluate("location.hash='chat'"); await delay(200); await screenshot('06-mobile');
  if (await evaluate('document.documentElement.scrollWidth > innerWidth + 1')) throw new Error('Mobile layout overflows horizontally.');
  checks.push('mobile layout has no horizontal overflow');
  await evaluate("document.getElementById('account-button').click(); document.getElementById('sign-out').click();");
  await until('logout disables composer and clears account UI', "document.getElementById('chat-text').disabled && document.getElementById('account-button').textContent.trim() === 'Sign in'");
  if (failures.length) throw new Error('Website raised JavaScript exceptions.');
  result = { result: 'PASS', checks, failures, browser: path.basename(browser), loopbackOnly: true };
} catch (error) { result = { result: 'FAIL', checks, failures, error: String(error) }; }
finally {
  if (socket?.readyState === WebSocket.OPEN) await Promise.race([command('Browser.close', {}, null).catch(() => {}), delay(3000)]);
  socket?.close();
  if (child.exitCode === null) { await Promise.race([new Promise(resolve => child.once('exit', resolve)), delay(5000)]); if (child.exitCode === null) { result.retainedBrowserPid = child.pid; child.unref(); } }
  await fs.writeFile(path.join(output, 'website-receipt.json'), JSON.stringify(result, null, 2));
}
console.log(JSON.stringify(result, null, 2)); process.exitCode = result.result === 'PASS' ? 0 : 1;
