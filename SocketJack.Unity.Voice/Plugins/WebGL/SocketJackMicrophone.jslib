mergeInto(LibraryManager.library, {
  $SJMic: {
    instances: {},
    remove: function(record, key) {
      var speaker = record.speakers[key]; if (!speaker) return; delete record.speakers[key];
      speaker.sources.forEach(function(source) { try { source.stop(); source.disconnect(); } catch (error) {} });
      speaker.sources.length = 0; speaker.gain.disconnect(); speaker.panner.disconnect();
    },
    stop: function(record) {
      record.captureGeneration++; record.pending = false; record.talking = false;
      if (record.processor) { record.processor.onaudioprocess = null; record.processor.disconnect(); record.processor = null; }
      if (record.input) { record.input.disconnect(); record.input = null; }
      if (record.silent) { record.silent.disconnect(); record.silent = null; }
      if (record.stream) { record.stream.getTracks().forEach(function(track) { track.onended = null; track.stop(); }); record.stream = null; }
      if (record.pointer) { _free(record.pointer); record.pointer = 0; }
    }
  },
  SJMic_Create__deps: ['$SJMic'],
  SJMic_Create: function(id, state, samples) {
    try {
      var Audio = window.AudioContext || window.webkitAudioContext;
      if (!Audio) { {{{ makeDynCall('vii', 'state') }}}(id, 2); return; }
      var context = new Audio({ latencyHint: 'interactive' });
      var record = { context: context, speakers: {}, played: 0, captureGeneration: 0, pending: false, talking: false, pointer: 0 };
      record.state = function(code) { {{{ makeDynCall('vii', 'state') }}}(id, code); };
      record.samples = function(pointer) { {{{ makeDynCall('viii', 'samples') }}}(id, pointer, 320); };
      record.analyser = context.createAnalyser(); record.analyser.fftSize = 512; record.analyser.connect(context.destination); record.analysis = new Float32Array(512);
      // Resuming playback on a game gesture never requests microphone permission.
      record.resume = function() { if (context.state === 'suspended') context.resume().catch(function() {}); };
      window.addEventListener('pointerdown', record.resume); window.addEventListener('keydown', record.resume);
      SJMic.instances[id] = record;
    } catch (error) { {{{ makeDynCall('vii', 'state') }}}(id, 4); }
  },
  SJMic_Resume__deps: ['$SJMic'],
  SJMic_Resume: function(id) { var record = SJMic.instances[id]; if (record) record.resume(); },
  SJMic_SetTalking__deps: ['$SJMic'],
  SJMic_SetTalking: function(id, value) { var record = SJMic.instances[id]; if (record) record.talking = value !== 0; },
  SJMic_StartCapture__deps: ['$SJMic'],
  SJMic_StartCapture: function(id) {
    var record = SJMic.instances[id]; if (!record || record.pending || record.stream) return;
    if (!window.isSecureContext || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { record.state(2); return; }
    record.pending = true; var generation = ++record.captureGeneration; record.resume();
    navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false }).then(function(stream) {
      if (SJMic.instances[id] !== record || generation !== record.captureGeneration) { stream.getTracks().forEach(function(track) { track.stop(); }); return; }
      try {
        record.stream = stream; record.pending = false; record.pointer = _malloc(320 * 4); if (!record.pointer) throw new Error('capture allocation');
        record.input = record.context.createMediaStreamSource(stream);
        // ScriptProcessor is the compatibility path for 2022-era WebGL hosts with restrictive CSP;
        // it needs no blob/eval/worklet URL. Its work and managed callbacks are strictly bounded.
        record.processor = record.context.createScriptProcessor(1024, 1, 1); record.silent = record.context.createGain(); record.silent.gain.value = 0;
        var frame = new Float32Array(320), count = 0, next = 0, previous = 0, ratio = record.context.sampleRate / 16000;
        record.processor.onaudioprocess = function(event) {
          if (SJMic.instances[id] !== record || !record.stream) return;
          if (!record.talking) { count = 0; next = 0; previous = 0; return; }
          var input = event.inputBuffer.getChannelData(0), delivered = 0;
          for (var i = 0; i < input.length; i++) {
            var value = Number.isFinite(input[i]) ? Math.max(-1, Math.min(1, input[i])) : 0;
            while (next <= i) {
              var alpha = Math.max(0, Math.min(1, next - i + 1)); frame[count++] = previous + (value - previous) * alpha; next += ratio;
              if (count === 320) {
                if (delivered++ < 4 && record.pointer) { HEAPF32.set(frame, record.pointer >> 2); record.samples(record.pointer); }
                count = 0;
              }
            }
            previous = value;
          }
          next -= input.length;
        };
        record.input.connect(record.processor); record.processor.connect(record.silent); record.silent.connect(record.context.destination);
        stream.getAudioTracks().forEach(function(track) { track.onended = function() { if (record.stream) { SJMic.stop(record); record.state(4); } }; });
        record.state(1);
      } catch (error) { SJMic.stop(record); record.state(4); }
    }).catch(function() { if (SJMic.instances[id] === record && generation === record.captureGeneration) { record.pending = false; record.state(3); } });
  },
  SJMic_StopCapture__deps: ['$SJMic'],
  SJMic_StopCapture: function(id) { var record = SJMic.instances[id]; if (record) { SJMic.stop(record); record.state(0); } },
  SJMic_SetListener__deps: ['$SJMic'],
  SJMic_SetListener: function(id, x, y, z, fx, fy, fz, ux, uy, uz) {
    var record = SJMic.instances[id]; if (!record) return; var listener = record.context.listener;
    if (listener.positionX) { listener.positionX.value = x; listener.positionY.value = y; listener.positionZ.value = z; listener.forwardX.value = fx; listener.forwardY.value = fy; listener.forwardZ.value = fz; listener.upX.value = ux; listener.upY.value = uy; listener.upZ.value = uz; }
    else { listener.setPosition(x, y, z); listener.setOrientation(fx, fy, fz, ux, uy, uz); }
  },
  SJMic_Play__deps: ['$SJMic'],
  SJMic_Play: function(id, keyPointer, pointer, count, spatial, x, y, z, volume) {
    var record = SJMic.instances[id]; if (!record || count !== 320 || record.context.state !== 'running') return;
    var key = UTF8ToString(keyPointer), speaker = record.speakers[key];
    if (!speaker) {
      if (Object.keys(record.speakers).length >= 8) return;
      var gain = record.context.createGain(), panner = record.context.createPanner();
      panner.panningModel = 'equalpower'; panner.distanceModel = 'inverse'; panner.refDistance = 1; panner.maxDistance = 30; panner.rolloffFactor = 1;
      gain.connect(spatial ? panner : record.analyser); panner.connect(record.analyser); speaker = record.speakers[key] = { gain: gain, panner: panner, spatial: !!spatial, sources: [], next: 0 };
    }
    var now = record.context.currentTime;
    if (speaker.next > now + .16 || speaker.sources.length >= 8) return;
    if (speaker.spatial !== !!spatial) { speaker.gain.disconnect(); speaker.gain.connect(spatial ? speaker.panner : record.analyser); speaker.spatial = !!spatial; }
    speaker.gain.gain.value = Number.isFinite(volume) ? Math.max(0, Math.min(1, volume)) : 0;
    speaker.panner.rolloffFactor = spatial ? 1 : 0;
    speaker.panner.setPosition(spatial ? x : 0, spatial ? y : 0, spatial ? z : 0);
    var buffer = record.context.createBuffer(1, count, 16000), data = buffer.getChannelData(0), source = record.context.createBufferSource();
    var input = HEAPF32.subarray(pointer >> 2, (pointer >> 2) + count);
    for (var i = 0; i < count; i++) data[i] = Number.isFinite(input[i]) ? Math.max(-1, Math.min(1, input[i])) : 0;
    source.buffer = buffer; source.connect(speaker.gain); speaker.sources.push(source);
    source.onended = function() { var at = speaker.sources.indexOf(source); if (at >= 0) speaker.sources.splice(at, 1); source.disconnect(); };
    var when = Math.max(now + .02, speaker.next); source.start(when); speaker.next = when + count / 16000; record.played += count;
  },
  SJMic_RemoveSpeaker__deps: ['$SJMic'],
  SJMic_RemoveSpeaker: function(id, keyPointer) { var record = SJMic.instances[id]; if (record) SJMic.remove(record, UTF8ToString(keyPointer)); },
  SJMic_PlaybackSamples__deps: ['$SJMic'],
  SJMic_PlaybackSamples: function(id) { var record = SJMic.instances[id]; return record ? record.played : 0; },
  SJMic_AudioTime__deps: ['$SJMic'],
  SJMic_AudioTime: function(id) { var record = SJMic.instances[id]; return record && record.context.state === 'running' ? record.context.currentTime : 0; },
  SJMic_OutputPeak__deps: ['$SJMic'],
  SJMic_OutputPeak: function(id) { var record = SJMic.instances[id]; if (!record) return 0; record.analyser.getFloatTimeDomainData(record.analysis); var peak = 0; for (var i = 0; i < record.analysis.length; i++) peak = Math.max(peak, Math.abs(record.analysis[i])); return peak; },
  SJMic_Destroy__deps: ['$SJMic'],
  SJMic_Destroy: function(id) {
    var record = SJMic.instances[id]; if (!record) return; delete SJMic.instances[id]; SJMic.stop(record);
    Object.keys(record.speakers).forEach(function(key) { SJMic.remove(record, key); });
    window.removeEventListener('pointerdown', record.resume); window.removeEventListener('keydown', record.resume);
    record.analyser.disconnect(); record.context.close().catch(function() {});
  }
});
