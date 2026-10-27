// v2.7: headless Chrome (CDP, real time) runs epikchat-tap.html against ws-server.js; writes the page record to argv[2].
if (typeof WebSocket === 'undefined' && !process.execArgv.includes('--experimental-websocket')) {   // node 20
  const r = require('child_process').spawnSync(process.execPath, ['--experimental-websocket', ...process.argv.slice(1)], { stdio: 'inherit' }); process.exit(r.status ?? 1);
}
const { spawn } = require('child_process'), fs = require('fs'), path = require('path'), os = require('os');
const out = process.argv[2] || path.join(os.tmpdir(), 'tap-chrome.json');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tapmock-'));
const here = __dirname, web = path.join(here, '../../EpikStreamer/Web');
fs.copyFileSync(path.join(here, 'epikchat-tap.html'), path.join(dir, 'epikchat-tap.html'));
fs.copyFileSync(path.join(here, 'msgpack-mini.js'), path.join(dir, 'msgpack-mini.js'));
fs.writeFileSync(path.join(dir, 'chat-tap.js'), 'window.__EPIK_TAP_CFG={"restPoll":false,"restPollSeconds":20,"map":{}};\n' + fs.readFileSync(path.join(web, 'chat-tap.js')));
fs.writeFileSync(path.join(dir, 'chat-observer.js'), 'window.__EPIK_CFG={"debug":true,"selfName":""};\n' + fs.readFileSync(path.join(web, 'chat-observer.js')));
const kill = [];
const fail = m => { console.error(m); kill.forEach(p => { try { p.kill('SIGKILL'); } catch { } }); process.exit(1); };
setTimeout(() => fail('timeout'), 60000);
const ws = spawn('node', [path.join(here, 'ws-server.js'), '0', '70']); kill.push(ws);
ws.stdout.once('data', async d => {
  const port = /PORT (\d+)/.exec(String(d))[1];
  const cdp = 9300 + Math.floor(Math.random() * 500);
  const ch = spawn('google-chrome', ['--headless=new', '--no-sandbox', '--disable-gpu', '--remote-debugging-port=' + cdp, '--user-data-dir=' + path.join(dir, 'prof'), 'about:blank'], { stdio: 'ignore' }); kill.push(ch);
  let tgt; for (let i = 0; i < 50 && !tgt; i++) { await new Promise(r => setTimeout(r, 200)); try { tgt = (await (await fetch(`http://127.0.0.1:${cdp}/json`)).json()).find(t => t.type === 'page'); } catch { } }
  if (!tgt) fail('no chrome');
  const s = new WebSocket(tgt.webSocketDebuggerUrl); let n = 0; const pend = new Map();
  const call = (method, params = {}) => new Promise(r => { const id = ++n; pend.set(id, r); s.send(JSON.stringify({ id, method, params })); });
  s.onmessage = e => { const m = JSON.parse(e.data); if (pend.has(m.id)) { pend.get(m.id)(m.result); pend.delete(m.id); } };
  await new Promise(r => s.onopen = r);
  await call('Page.navigate', { url: `file://${dir}/epikchat-tap.html#port=${port}` });
  for (let i = 0; i < 100; i++) {
    await new Promise(r => setTimeout(r, 300));
    const r = await call('Runtime.evaluate', { expression: 'document.title', returnByValue: true });
    const t = r && r.result && r.result.value || '';
    if (t.startsWith('{')) { fs.writeFileSync(out, t); console.log('page record → ' + out + ' (' + t.length + ' chars)'); kill.forEach(p => p.kill('SIGKILL')); process.exit(0); }
  }
  fail('page never finished');
});
