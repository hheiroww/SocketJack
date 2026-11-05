mergeInto(LibraryManager.library, {
  $SJVoiceSocket: { sockets: {} },
  SJVoiceSocket_Open__deps: ['$SJVoiceSocket'],
  SJVoiceSocket_Open: function(id, address, opened, closed, failed, message, maximum) {
    try {
      var socket = new WebSocket(UTF8ToString(address));
      socket.binaryType = 'arraybuffer'; SJVoiceSocket.sockets[id] = socket;
      socket.onopen = function() { {{{ makeDynCall('vi', 'opened') }}}(id); };
      socket.onclose = function() { delete SJVoiceSocket.sockets[id]; {{{ makeDynCall('vi', 'closed') }}}(id); };
      socket.onerror = function() { {{{ makeDynCall('vi', 'failed') }}}(id); };
      socket.onmessage = function(event) {
        var text = typeof event.data === 'string';
        var count = text ? lengthBytesUTF8(event.data) : event.data instanceof ArrayBuffer ? event.data.byteLength : -1;
        if (count < 0 || count > maximum) { {{{ makeDynCall('vi', 'failed') }}}(id); socket.close(1000, 'message limit'); return; }
        var pointer = _malloc(Math.max(1, count));
        if (!pointer) { {{{ makeDynCall('vi', 'failed') }}}(id); return; }
        try {
          if (text) { var encoded = new TextEncoder().encode(event.data); HEAPU8.set(encoded, pointer); }
          else HEAPU8.set(new Uint8Array(event.data), pointer);
          {{{ makeDynCall('viiii', 'message') }}}(id, pointer, count, text ? 1 : 0);
        } finally { _free(pointer); }
      };
    } catch (error) { {{{ makeDynCall('vi', 'failed') }}}(id); }
  },
  SJVoiceSocket_Send__deps: ['$SJVoiceSocket'],
  SJVoiceSocket_Send: function(id, pointer, count, text, bufferedLimit) {
    var socket = SJVoiceSocket.sockets[id];
    if (!socket || socket.readyState !== WebSocket.OPEN) return -1;
    if (count < 0 || count > 1024 || socket.bufferedAmount + count > bufferedLimit) return 1;
    try { socket.send(text ? UTF8ArrayToString(HEAPU8, pointer, count) : HEAPU8.slice(pointer, pointer + count)); return 0; }
    catch (error) { return -1; }
  },
  SJVoiceSocket_Close__deps: ['$SJVoiceSocket'],
  SJVoiceSocket_Close: function(id) {
    var socket = SJVoiceSocket.sockets[id]; delete SJVoiceSocket.sockets[id];
    if (socket) { socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null; try { socket.close(1000, 'closed'); } catch (error) {} }
  }
});
