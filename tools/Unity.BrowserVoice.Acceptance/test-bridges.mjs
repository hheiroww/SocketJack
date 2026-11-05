import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';

// Test the repo-native JavaScript bridge without requesting hardware or a browser permission.
const memory = new ArrayBuffer(65536), heap = new Uint8Array(memory), samples = new Float32Array(memory);
const library = {}, states = [], capture = [], frees = [], sources = [];
let mediaRequests = 0, tracksStopped = 0;
class Node {
  constructor() { this.connections=[]; this.gain={value:1}; }
  connect(node) { this.connections.push(node); }
  disconnect() { this.connections=[]; }
}
class Audio {
  constructor() { this.sampleRate=48000; this.currentTime=0; this.state='running'; this.destination={}; this.listener={setPosition(){},setOrientation(){}}; }
  createAnalyser() { return Object.assign(new Node(),{getFloatTimeDomainData(data){data.fill(.2);}}); }
  createGain() { return new Node(); }
  createPanner() { return Object.assign(new Node(),{setPosition(){}}); }
  createScriptProcessor() { return new Node(); }
  createMediaStreamSource() { return new Node(); }
  createBuffer(channels,count) { const data=new Float32Array(count); return {getChannelData(){return data;}}; }
  createBufferSource() { const source=Object.assign(new Node(),{start(at){this.at=at;},stop(){this.stopped=true;}});sources.push(source);return source; }
  resume() { this.state='running';return Promise.resolve(); }
  close() { this.state='closed';return Promise.resolve(); }
}
class Socket {
  static OPEN=1;
  constructor() { this.readyState=0;this.bufferedAmount=0;this.sent=[]; }
  send(value) { this.sent.push(value); }
  close() { this.readyState=3; }
}
const track={stop(){tracksStopped++;},onended:null};
const context=vm.createContext({
  LibraryManager:{library},mergeInto(target,values){Object.assign(target,values);},
  window:{AudioContext:Audio,isSecureContext:true,addEventListener(){},removeEventListener(){}},
  navigator:{mediaDevices:{async getUserMedia(constraints){mediaRequests++;assert.equal(constraints.video,false);return {getTracks(){return [track];},getAudioTracks(){return [track];}};}}},
  WebSocket:Socket,ArrayBuffer,Uint8Array,Float32Array,TextEncoder,
  HEAPU8:heap,HEAPF32:samples,_malloc(){return 4096;},_free(pointer){frees.push(pointer);},
  UTF8ToString(value){return String(value);},lengthBytesUTF8(value){return new TextEncoder().encode(value).length;},
  UTF8ArrayToString(bytes,offset,count){return new TextDecoder().decode(bytes.subarray(offset,offset+count));}
});
for(const file of ['../../SocketJack.Unity.Voice/Plugins/WebGL/SocketJackMicrophone.jslib','../../SocketJack.Unity.Voice.Web.Client/Plugins/WebGL/SocketJackVoiceSocket.jslib']){
  const code=(await fs.readFile(new URL(file,import.meta.url),'utf8')).replace(/\{\{\{\s*makeDynCall\('[^']+',\s*'([^']+)'\)\s*\}\}\}/g,'($1)');
  vm.runInContext(code,context);
}
context.SJMic=library.$SJMic;context.SJVoiceSocket=library.$SJVoiceSocket;
library.SJMic_Create(1,(id,state)=>states.push(state),(id,pointer,count)=>capture.push(samples.slice(pointer/4,pointer/4+count)));
assert.equal(mediaRequests,0,'Adding the bridge must never request a microphone.');
library.SJMic_StartCapture(1);await new Promise(resolve=>setImmediate(resolve));
assert.equal(mediaRequests,1);assert.ok(states.includes(1));
const record=context.SJMic.instances[1], input=Float32Array.from({length:1024},(_,i)=>.2*Math.sin(i*2*Math.PI*440/48000));
for(let i=0;i<3;i++)record.processor.onaudioprocess({inputBuffer:{getChannelData(){return input;}}});
assert.equal(capture.length,0,'PTT-off samples must stay outside managed capture.');
library.SJMic_SetTalking(1,1);
for(let i=0;i<3;i++)record.processor.onaudioprocess({inputBuffer:{getChannelData(){return input;}}});
assert.equal(capture.length,3);assert.ok(capture.every(frame=>frame.length===320&&frame.every(Number.isFinite)));
samples.fill(.2,2048,2368);
for(let i=0;i<20;i++)library.SJMic_Play(1,'speaker',8192,320,0,0,0,0,1);
assert.equal(record.speakers.speaker.sources.length,8,'Browser output scheduling must stay bounded.');
assert.equal(record.played,2560);assert.equal(record.speakers.speaker.gain.connections[0],record.analyser,'Nonspatial voices must bypass positional panning.');
library.SJMic_RemoveSpeaker(1,'speaker');assert.ok(sources.every(source=>source.stopped));
library.SJMic_StopCapture(1);assert.equal(tracksStopped,1);assert.equal(record.stream,null);assert.ok(frees.includes(4096));
library.SJMic_Destroy(1);assert.equal(record.context.state,'closed');
let opens=0,failures=0;const messages=[];
library.SJVoiceSocket_Open(2,'ws://127.0.0.1/ws/voice',()=>opens++,()=>{},()=>failures++,(id,pointer,count,text)=>messages.push({count,text}),1024);
const socket=context.SJVoiceSocket.sockets[2];socket.readyState=Socket.OPEN;socket.onopen();assert.equal(opens,1);
socket.onmessage({data:'auth'});socket.onmessage({data:new Uint8Array([1,2,3]).buffer});assert.deepEqual(messages,[{count:4,text:1},{count:3,text:0}]);
socket.bufferedAmount=2048;assert.equal(library.SJVoiceSocket_Send(2,0,100,0,2048),1);assert.equal(socket.sent.length,0);
socket.bufferedAmount=0;assert.equal(library.SJVoiceSocket_Send(2,0,100,0,2048),0);assert.equal(socket.sent[0].length,100);
socket.onmessage({data:new Uint8Array(1025).buffer});assert.equal(failures,1);library.SJVoiceSocket_Close(2);
console.log('PASS browser bridges: explicit capture, PTT, 48k-to-16k frames, bounded playback/cleanup, text+binary sockets, backpressure and malformed-size rejection.');
