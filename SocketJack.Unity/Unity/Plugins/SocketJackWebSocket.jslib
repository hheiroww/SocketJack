mergeInto(LibraryManager.library, {
  $SJWS: { sockets: {} },
  SJWS_Open__deps: ['$SJWS'],
  SJWS_Open: function(id, url, opened, closed, failed, message, maximumBytes) {
    try {
      var socket = new WebSocket(UTF8ToString(url));
      socket.binaryType = 'arraybuffer';
      SJWS.sockets[id] = socket;
      socket.onopen = function() { {{{ makeDynCall('vi', 'opened') }}}(id); };
      socket.onclose = function() { delete SJWS.sockets[id]; {{{ makeDynCall('vi', 'closed') }}}(id); };
      socket.onerror = function() { {{{ makeDynCall('vi', 'failed') }}}(id); };
      socket.onmessage = function(event) {
        if (!(event.data instanceof ArrayBuffer) || event.data.byteLength > maximumBytes) {
          {{{ makeDynCall('vi', 'failed') }}}(id);
          socket.close(1000, 'message limit');
          return;
        }
        var count = event.data.byteLength;
        var ptr = _malloc(Math.max(1, count));
        try {
          HEAPU8.set(new Uint8Array(event.data), ptr);
          {{{ makeDynCall('viii', 'message') }}}(id, ptr, count);
        } finally { _free(ptr); }
      };
    } catch (error) { {{{ makeDynCall('vi', 'failed') }}}(id); }
  },
  SJWS_Send__deps: ['$SJWS'],
  SJWS_Send: function(id, pointer, offset, count, maximumBufferedBytes) {
    var socket = SJWS.sockets[id];
    if (!socket || socket.readyState !== WebSocket.OPEN) return -1;
    if (count < 0 || count > maximumBufferedBytes || socket.bufferedAmount + count > maximumBufferedBytes) return 1;
    try { socket.send(HEAPU8.slice(pointer + offset, pointer + offset + count)); return 0; }
    catch (error) { return -1; }
  },
  SJWS_Close__deps: ['$SJWS'],
  SJWS_Close: function(id) {
    var socket = SJWS.sockets[id];
    delete SJWS.sockets[id];
    if (socket) {
      socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
      try { socket.close(1000, 'closed'); } catch (error) {}
    }
  }
});
