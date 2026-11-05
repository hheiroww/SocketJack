import fs from 'node:fs/promises';
import path from 'node:path';
import http from 'node:http';
import { spawn } from 'node:child_process';

const [browser, webRoot, output, gamePortText, httpPortText, relayMode] = process.argv.slice(2);
if (!browser || !webRoot || !output) throw new Error('Supply browser, WebGL root, owned output directory, gamePort and httpPort.');
const gamePort = Number(gamePortText), httpPort = Number(httpPortText);
const root = path.resolve(webRoot), profile = path.join(path.resolve(output), 'browser-profile');
await fs.mkdir(profile, { recursive: false });
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    const relative = decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname);
    const file = path.resolve(root, '.' + relative);
    if (!file.startsWith(root + path.sep)) { response.writeHead(403); response.end(); return; }
    const data = await fs.readFile(file);
    const types = { '.html':'text/html', '.js':'text/javascript', '.wasm':'application/wasm', '.data':'application/octet-stream', '.css':'text/css' };
    response.writeHead(200, { 'Content-Type':types[path.extname(file)] || 'application/octet-stream', 'Content-Length':data.length, 'Cache-Control':'no-store' }); response.end(data);
  } catch { response.writeHead(404); response.end(); }
});
await new Promise(resolve => server.listen(httpPort, '127.0.0.1', resolve));
const child = spawn(browser, ['--headless=new','--no-first-run','--disable-default-apps','--disable-extensions','--disable-dev-shm-usage','--use-gl=angle','--use-angle=swiftshader','--enable-unsafe-swiftshader','--ignore-gpu-blocklist','--remote-debugging-port=0',`--user-data-dir=${profile}`,'about:blank'], { stdio:'ignore', windowsHide:true });
let socket, result = { result:'FAIL' }, sequence = 0;
const pending = new Map(), logs = [];
function command(method, params = {}, sessionId) {
  return new Promise((resolve, reject) => { const id = ++sequence; pending.set(id, {resolve,reject}); socket.send(JSON.stringify({id,method,params,...(sessionId ? {sessionId} : {})})); });
}
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
try {
  let endpoint;
  for (let tries=0; tries<100; tries++) { try { const lines=(await fs.readFile(path.join(profile,'DevToolsActivePort'),'utf8')).trim().split(/\r?\n/); endpoint=`ws://127.0.0.1:${lines[0]}${lines[1]}`; break; } catch { await delay(100); } }
  if (!endpoint) throw new Error('Owned headless browser did not open DevTools.');
  socket = new WebSocket(endpoint); await new Promise((resolve,reject) => { socket.onopen=resolve; socket.onerror=reject; });
  socket.onmessage = event => { const message=JSON.parse(event.data); if(message.id && pending.has(message.id)){const waiter=pending.get(message.id);pending.delete(message.id);message.error?waiter.reject(new Error(JSON.stringify(message.error))):waiter.resolve(message.result);}else if(message.method==='Runtime.consoleAPICalled')logs.push(message.params.args.map(a=>a.value??a.description??'').join(' '));else if(message.method==='Runtime.exceptionThrown')logs.push('EXCEPTION '+JSON.stringify(message.params.exceptionDetails));else if(message.method?.startsWith('Network.webSocket')&&logs.length<500){const p=message.params;logs.push(message.method+' '+JSON.stringify({status:p.response?.status,opcode:p.response?.opcode,payloadLength:p.response?.payloadData?.length,error:p.errorMessage}));} };
  const target=await command('Target.createTarget',{url:'about:blank'}); const {sessionId}=await command('Target.attachToTarget',{targetId:target.targetId,flatten:true});
  await command('Runtime.enable',{},sessionId); await command('Page.enable',{},sessionId); await command('Network.enable',{},sessionId);
  await command('Page.navigate',{url:`http://127.0.0.1:${httpPort}/?endpoint=${encodeURIComponent(`ws://127.0.0.1:${gamePort}/ws/${relayMode==='relay'?'relay':'game'}`)}${relayMode==='relay'?'&relay=1':''}`},sessionId);
  let status='';
  for(let tries=0;tries<180;tries++){await delay(500);const value=await command('Runtime.evaluate',{expression:"document.body && document.body.getAttribute('data-socketjack-probe')",returnByValue:true},sessionId);status=value.result?.value??'';if(status)break;}
  result={result:status.startsWith('PASS ')?'PASS':'FAIL',status,logs,browser:path.basename(browser),gamePort,httpPort,relay:relayMode==='relay',actualWebGL:true};
  await Promise.race([command('Browser.close').catch(()=>{}),delay(3000)]);
} catch(error) { result={...result,error:String(error),logs}; if(socket?.readyState===WebSocket.OPEN)try{await Promise.race([command('Browser.close').catch(()=>{}),delay(3000)]);}catch{} }
finally {
  socket?.close(); await new Promise(resolve=>server.close(resolve)); await fs.writeFile(path.join(output,'browser-receipt.json'),JSON.stringify(result,null,2));
  if(child.exitCode===null){await Promise.race([new Promise(resolve=>child.once('exit',resolve)),delay(10000)]);if(child.exitCode===null){result.retainedBrowserPid=child.pid;child.unref();await fs.writeFile(path.join(output,'browser-receipt.json'),JSON.stringify(result,null,2));}}
}
console.log(JSON.stringify(result,null,2));
process.exitCode=result.result==='PASS'?0:1;
