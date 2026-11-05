mergeInto(LibraryManager.library, {
  $SocketJackApiSockets: {},
  SJWebApi_Open__deps: ['$SocketJackApiSockets'],
  SJWebApi_Open: function (id, uriPointer, targetPointer) {
    var uri = UTF8ToString(uriPointer), target = UTF8ToString(targetPointer);
    try {
      var socket = new WebSocket(uri);
      SocketJackApiSockets[id] = socket;
      socket.onopen = function () { SendMessage(target, 'OnSocketOpen', String(id)); };
      socket.onmessage = function (event) {
        if (typeof event.data !== 'string' || event.data.length > 131072) { socket.close(1009, 'Bounded text API only'); return; }
        SendMessage(target, 'OnSocketMessage', String(id) + '|' + event.data);
      };
      socket.onerror = function () { SendMessage(target, 'OnSocketError', String(id)); };
      socket.onclose = function () { delete SocketJackApiSockets[id]; SendMessage(target, 'OnSocketClose', String(id)); };
    } catch (_) { SendMessage(target, 'OnSocketError', String(id)); }
  },
  SJWebApi_Send__deps: ['$SocketJackApiSockets'],
  SJWebApi_Send: function (id, messagePointer) {
    var socket = SocketJackApiSockets[id];
    if (!socket || socket.readyState !== WebSocket.OPEN || socket.bufferedAmount > 262144) return 0;
    try { socket.send(UTF8ToString(messagePointer)); return 1; } catch (_) { return 0; }
  },
  SJWebApi_Close__deps: ['$SocketJackApiSockets'],
  SJWebApi_Close: function (id) {
    var socket = SocketJackApiSockets[id]; delete SocketJackApiSockets[id];
    if (!socket) return;
    socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
    try { socket.close(1000, 'Client disposed'); } catch (_) { }
  }
});
