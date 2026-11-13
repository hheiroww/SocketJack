"use strict";
// WebSocketClientOptions
class WebSocketClientOptions {
    constructor() {
        this.logging = true;
        this.usePeerToPeer = true;
        this.downloadBufferSize = 65535;
        this.useCompression = true;
    }
}
// Identifier
class Identifier {
    WithParent(Client) {
        if (Client) {
            this.Parent = Client;
        }
        else {
            return null;
        }
        return this;
    }
    static Create(ID, IP = "", Metadata, PrivateMetadata, Parent) {
        let identifier = new Identifier();
        identifier.ID = ID;
        identifier.IP = IP;
        identifier.Metadata = Metadata !== null && Metadata !== void 0 ? Metadata : {};
        identifier.PrivateMetadata = PrivateMetadata !== null && PrivateMetadata !== void 0 ? PrivateMetadata : {};
        identifier.Parent = Parent !== null && Parent !== void 0 ? Parent : null;
        return identifier;
    }
    constructor() {
        this.ID = "";
        this.IP = "";
        this.Metadata = {};
        this.PrivateMetadata = {};
        this.Action = PeerAction.RemoteIdentity;
    }
    SetMetaData(key, value, isPrivate) {
        if (isPrivate) {
            if (!this.PrivateMetadata)
                this.PrivateMetadata = {};
            if (value === null) {
                delete this.PrivateMetadata[key];
            }
            else {
                this.PrivateMetadata[key] = value;
            }
        }
        else {
            if (value === null || value === "") {
                delete this.Metadata[key];
            }
            else {
                this.Metadata[key] = value;
            }
        }
    }
    GetMetaData(key, isPrivate) {
        if (isPrivate) {
            return this.PrivateMetadata ? this.PrivateMetadata[key] : undefined;
        }
        return this.Metadata[key];
    }
    GetMetaDataTyped(key, isPrivate) {
        const value = this.GetMetaData ? this.GetMetaData(key, isPrivate) : undefined;
        if (value === undefined)
            return undefined;
        try {
            return JSON.parse(value);
        }
        catch (_a) {
            return value;
        }
    }
}
// JSConstructors
class JSConstructors {
    constructor(Script) {
        this.Script = Script;
    }
}
// MetadataKeyValue
class MetadataKeyValue {
    constructor(Key, Value) {
        this.Key = Key;
        this.Value = Value;
    }
}
// PeerRedirect
class PeerRedirect {
    constructor(Recipient, Data) {
        this.Recipient = Recipient;
        this.Data = Data;
    }
}
// PeerAction
var PeerAction;
(function (PeerAction) {
    PeerAction[PeerAction["RemoteIdentity"] = 0] = "RemoteIdentity";
    PeerAction[PeerAction["Dispose"] = 1] = "Dispose";
    PeerAction[PeerAction["LocalIdentity"] = 2] = "LocalIdentity";
    PeerAction[PeerAction["MetadataUpdate"] = 3] = "MetadataUpdate";
})(PeerAction || (PeerAction = {}));
const PeerActionStrings = {
    [PeerAction.RemoteIdentity]: "remoteIdentity",
    [PeerAction.Dispose]: "dispose",
    [PeerAction.LocalIdentity]: "localIdentity",
    [PeerAction.MetadataUpdate]: "metadataUpdate"
};
// ServerInfo
class ServerInfo {
    constructor() {
        this.id = "";
        this.port = 0;
        this.name = "";
    }
}
if (typeof window !== 'undefined') {
    window.Identifier = Identifier;
    window.WebSocketClientOptions = WebSocketClientOptions;
    window.JSConstructors = JSConstructors;
    window.MetadataKeyValue = MetadataKeyValue;
    window.PeerRedirect = PeerRedirect;
    window.PeerAction = PeerAction;
    window.PeerActionStrings = PeerActionStrings;
    window.ServerInfo = ServerInfo;
}
class WebSocketClient {
    constructor(name) {
        this.options = new WebSocketClientOptions();
        this.name = "WebSocketClient";
        this.typeCallbacks = new Map();
        this.peers = new Map();
        this.p2pServers = new Map();
        this.p2pClients = new Map();
        this.p2pServerInformation = new Map();
        this.events = {};
        this.internalID = this._generateGuid();
        this.isDisposed = false;
        this.peerToPeerInstance = false;
        this.connected = false;
        this.socket = null;
        this.remoteIdentity = null;
        this.name = name;
        this.Init();
    }
    async Init() {
        this.registerCallback('MetadataKeyValue', (MetaDataCallback) => {
            if (MetaDataCallback && MetaDataCallback.Key && MetaDataCallback.Value) {
                if (this.remoteIdentity) {
                    this.remoteIdentity.Metadata[MetaDataCallback.Key] = MetaDataCallback.Value;
                }
                if (this.options.logging)
                    console.log(`[${this.name}] Metadata updated: ${MetaDataCallback.Key} = ${MetaDataCallback.Value}`);
            }
        });
    }
    on(event, handler) {
        if (!this.events[event])
            this.events[event] = [];
        this.events[event].push(handler);
    }
    off(event, handler) {
        if (!this.events[event])
            return;
        this.events[event] = this.events[event].filter(h => h !== handler);
    }
    _emit(event, ...args) {
        if (this.events[event]) {
            this.events[event].forEach(h => h(...args));
        }
    }
    async connect(url) {
        return new Promise((resolve, reject) => {
            this.socket = new WebSocket(url);
            this.socket.binaryType = 'arraybuffer';
            this.socket.onopen = () => {
                this.connected = true;
                this._emit('connected', this);
                if (this.options.logging)
                    console.log(`[${this.name}] Connected to WebSocket -> ${url}`);
                resolve(true);
            };
            this.socket.onerror = (err) => {
                this._emit('error', err);
                if (this.options.logging)
                    console.error(`[${this.name}] WebSocket error`, err);
                reject(err);
            };
            this.socket.onclose = (e) => {
                this.connected = false;
                this._emit('disconnected', e);
                if (this.options.logging)
                    console.log(`[${this.name}] Disconnected from WebSocket`);
            };
            this.socket.onmessage = (msg) => {
                let data = msg.data;
                if (this.options.useCompression) {
                    if (data instanceof ArrayBuffer) {
                        try {
                            // @ts-ignore
                            let decompressed = pako.ungzip(new Uint8Array(data), { to: 'string' });
                            data = decompressed;
                        }
                        catch (e) {
                            this._emit('error', e);
                            if (this.options.logging)
                                console.error(`[${this.name}] Decompression error`, e);
                        }
                    }
                }
                let obj;
                try {
                    obj = typeof data === 'string' ? JSON.parse(data) : data;
                    this._handleReceive(obj);
                }
                catch (e) {
                    if (this.options.logging)
                        console.error(`[${this.name}] Object deserialization error.`, e);
                }
            };
        });
    }
    _wrap(obj) {
        return `{"Type":"JSType.${obj.constructor.name}", "Value":${JSON.stringify(obj)}}`;
    }
    send(obj) {
        if (!this.connected || !this.socket)
            return;
        var wrapped = this._wrap(obj);
        if (this.options.useCompression) {
            // @ts-ignore
            if (typeof pako !== 'undefined') {
                try {
                    // @ts-ignore
                    var compressed = pako.gzip(wrapped);
                    this.socket.send(compressed.buffer);
                    this._emit('sent', obj);
                    if (this.options.logging)
                        console.log(`[${this.name}] Sent compressed object of type '${obj.constructor.name}'`);
                    return;
                }
                catch (e) {
                    this._emit('error', e);
                    if (this.options.logging)
                        console.error(`[${this.name}] Compression error`, e);
                }
            }
            else {
                this._emit('error', new Error('pako library not loaded'));
                if (this.options.logging)
                    console.error(`[${this.name}] pako library not loaded`);
            }
        }
        this.socket.send(wrapped);
        this._emit('sent', obj);
        if (this.options.logging)
            console.log(`[${this.name}] Sent object of type '${obj.constructor.name}'`);
    }
    sendBroadcast(obj) {
        for (let peer of this.peers.values()) {
            this.sendToPeer(peer, obj);
        }
    }
    sendToPeer(peer, obj) {
        // Simulate peer-to-peer by sending a message with recipient info
        let peerRedirect = new PeerRedirect(peer.id, obj);
        let wrapped = this._wrap(peerRedirect);
        this.send(wrapped);
        return peerRedirect;
    }
    sendSegmented(obj) {
        let wrapped = this._wrap(obj);
        let data = JSON.stringify(wrapped);
        let segmentSize = 1024;
        for (let i = 0; i < data.length; i += segmentSize) {
            let segment = data.slice(i, i + segmentSize);
            let segmentObj = { segment, index: i / segmentSize };
            let wrappedSegment = this._wrap(segmentObj);
            this.send(wrappedSegment);
        }
    }
    async startServer(id, options = {}, name = "WebSocketP2PServer", port = 0) {
        this._emit('error', new Error('WebSocketServer does not exist in SocketJack (yet).'));
        return null;
        //if (!this.options.usePeerToPeer) {
        //    this._emit('error', new Error('P2P is not enabled.'));
        //    return null;
        //}
        //// Simulate server info
        //let serverInfo: ServerInfo = { id, port, name };
        //this.p2pServerInformation.set(id, serverInfo);
        //this.p2pServers.set(id, serverInfo);
        //this._emit('peerServerStarted', serverInfo);
        //return serverInfo;
    }
    async acceptPeerConnection(server, name = "WebSocketP2PClient") {
        if (!this.options.usePeerToPeer) {
            this._emit('error', new Error('P2P is not enabled.'));
            return null;
        }
        let newClient = new WebSocketClient(name);
        this.p2pClients.set(server.id, newClient);
        newClient.name = name;
        try {
            await newClient.connect(`ws://${server.id}:${server.port}`);
            return newClient;
        }
        catch (err) {
            this._emit('error', err);
            return null;
        }
    }
    _handleReceive(obj) {
        if (obj && obj.type === 'Identifier') {
            this._handleIdentifier(obj);
        }
        else {
            this._emit('receive', obj);
        }
        this.invokeAllCallbacks(obj);
    }
    registerCallback(type, callback) {
        if (!this.typeCallbacks.has(type)) {
            this.typeCallbacks.set(type, []);
        }
        this.typeCallbacks.get(type).push(callback);
    }
    removeCallback(type, callback) {
        if (!this.typeCallbacks.has(type))
            return;
        const arr = this.typeCallbacks.get(type).filter(cb => cb !== callback);
        if (arr.length > 0) {
            this.typeCallbacks.set(type, arr);
        }
        else {
            this.typeCallbacks.delete(type);
        }
    }
    invokeAllCallbacks(obj) {
        const type = obj.constructor.name;
        if (type && this.typeCallbacks.has(type)) {
            this.typeCallbacks.get(type).forEach(cb => cb(obj));
        }
    }
    _handleIdentifier(pUpdate) {
        switch (pUpdate.Action) {
            case PeerAction.LocalIdentity:
                this.remoteIdentity = Identifier.Create(pUpdate.ID || '', pUpdate.IP || '', pUpdate.Metadata || {}, pUpdate.PrivateMetadata, this);
                this.remoteIdentity.Action = PeerAction.LocalIdentity;
                this._emit('identified', this.remoteIdentity);
                if (this.options.logging) {
                    console.log(`[${this.name}] Local identity set: ID=${this.remoteIdentity.ID}`);
                }
                break;
            case PeerAction.RemoteIdentity:
                const remotePeer = Identifier.Create(pUpdate.ID || '', pUpdate.IP || '', pUpdate.Metadata || {}, pUpdate.PrivateMetadata, this);
                remotePeer.Action = PeerAction.RemoteIdentity;
                const peerExists = this.peers.has(pUpdate.ID);
                this.peers.set(pUpdate.ID, remotePeer);
                if (!peerExists) {
                    this._emit('peerConnected', remotePeer);
                    if (this.options.logging) {
                        console.log(`[${this.name}] Peer Connected: ${remotePeer.ID}`);
                    }
                }
                this._emit('peerUpdated', remotePeer);
                break;
            case PeerAction.Dispose:
                this.peers.delete(pUpdate.ID);
                this.p2pClients.delete(pUpdate.ID);
                this.p2pServerInformation.delete(pUpdate.ID);
                this._emit('peerDisconnected', pUpdate);
                this._emit('peerUpdate', pUpdate);
                break;
            case PeerAction.MetadataUpdate:
                const metaPeer = Identifier.Create(pUpdate.ID || '', pUpdate.IP || '', pUpdate.Metadata || {}, pUpdate.PrivateMetadata, this);
                metaPeer.Action = PeerAction.MetadataUpdate;
                this.peers.set(pUpdate.ID, metaPeer);
                this._emit('peerMetaDataChanged', metaPeer);
                this._emit('peerUpdate', pUpdate);
                break;
            default:
                break;
        }
    }
    dispose() {
        if (this.connected && this.socket) {
            this.socket.close();
            this.isDisposed = true;
            this._emit('disposing');
        }
    }
    _generateGuid() {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }
}
if (typeof window !== 'undefined') {
    window.WebSocketClient = WebSocketClient;
}
