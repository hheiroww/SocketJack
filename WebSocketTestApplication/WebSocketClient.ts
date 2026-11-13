// WebSocketClientOptions
class WebSocketClientOptions {
    public logging: boolean = true;
    public usePeerToPeer: boolean = true;
    public downloadBufferSize: number = 65535;
    public useCompression: boolean = true;
}

// Identifier
class Identifier {
    public ID: string = "";
    public IP: string = "";
    public Metadata: { [key: string]: string } = {};
    public PrivateMetadata?: { [key: string]: string } = {};
    public Action?: PeerAction = PeerAction.RemoteIdentity;
    public Parent?: WebSocketClient | null;
    public WithParent(Client: WebSocketClient): Identifier | null {
        if (Client) {
            this.Parent = Client;
        } else {
            return null;
        }
        return this;
    }

    static Create(ID: string, IP: string = "", Metadata?: { [key: string]: string }, PrivateMetadata?: { [key: string]: string }, Parent?: WebSocketClient): Identifier {
        let identifier = new Identifier();
        identifier.ID = ID;
        identifier.IP = IP;
        identifier.Metadata = Metadata ?? {};
        identifier.PrivateMetadata = PrivateMetadata ?? {};
        identifier.Parent = Parent ?? null;
        return identifier;
    }

    constructor() { }

    public SetMetaData(key: string, value: string | null, isPrivate?: boolean) {
        if (isPrivate) {
            if (!this.PrivateMetadata) this.PrivateMetadata = {};
            if (value === null) {
                delete this.PrivateMetadata[key];
            } else {
                this.PrivateMetadata[key] = value;
            }
        } else {
            if (value === null || value === "") {
                delete this.Metadata[key];
            } else {
                this.Metadata[key] = value;
            }
        }
    }

    public GetMetaData(key: string, isPrivate?: boolean): string | undefined {
        if (isPrivate) {
            return this.PrivateMetadata ? this.PrivateMetadata[key] : undefined;
        }
        return this.Metadata[key];
    }

    GetMetaDataTyped<T>(key: string, isPrivate?: boolean): T | undefined {
        const value = this.GetMetaData ? this.GetMetaData(key, isPrivate) : undefined;
        if (value === undefined) return undefined;
        try {
            return JSON.parse(value) as T;
        } catch {
            return value as unknown as T;
        }
    }
}

// JSConstructors
class JSConstructors {
    public Script: string;
    constructor(Script: string) {
        this.Script = Script;
    }
}

// MetadataKeyValue
class MetadataKeyValue {
    Key: string;
    Value: string;
    constructor(Key: string, Value: string) {
        this.Key = Key;
        this.Value = Value;
    }
}

// PeerRedirect
class PeerRedirect {
    public Recipient: string;
    public Data: string;
    constructor(Recipient: string, Data: string) {
        this.Recipient = Recipient;
        this.Data = Data;
    }
}

// PeerAction
enum PeerAction {
    RemoteIdentity = 0,
    Dispose = 1,
    LocalIdentity = 2,
    MetadataUpdate = 3
}

const PeerActionStrings: { [key in PeerAction]: string } = {
    [PeerAction.RemoteIdentity]: "remoteIdentity",
    [PeerAction.Dispose]: "dispose",
    [PeerAction.LocalIdentity]: "localIdentity",
    [PeerAction.MetadataUpdate]: "metadataUpdate"
};

// ServerInfo
class ServerInfo {
    public id: string = "";
    public port: number = 0;
    public name: string = "";
    constructor() { }
}

if (typeof window !== 'undefined') {
    (window as any).Identifier = Identifier;
    (window as any).WebSocketClientOptions = WebSocketClientOptions;
    (window as any).JSConstructors = JSConstructors;
    (window as any).MetadataKeyValue = MetadataKeyValue;
    (window as any).PeerRedirect = PeerRedirect;
    (window as any).PeerAction = PeerAction;
    (window as any).PeerActionStrings = PeerActionStrings;
    (window as any).ServerInfo = ServerInfo;
}

// WebSocketClient.ts
// Replicates the C# WebSocketClient class with events and P2P functionality
type EventHandler = (...args: any[]) => void;
type Callback<T = any> = (obj: T) => void;
class WebSocketClient {
    public options: WebSocketClientOptions = new WebSocketClientOptions();
    public name: string = "WebSocketClient";
    public typeCallbacks: Map<string, Callback[]> = new Map<string, Callback[]>();
    public peers: Map<string, Identifier> = new Map<string, Identifier>();
    public p2pServers: Map<string, ServerInfo> = new Map<string, ServerInfo>();
    public p2pClients: Map<string, WebSocketClient> = new Map<string, WebSocketClient>();
    public p2pServerInformation: Map<string, ServerInfo> = new Map<string, ServerInfo>();
    public events: { [event: string]: EventHandler[] } = {};
    public internalID: string = this._generateGuid();
    public isDisposed: boolean = false;
    public peerToPeerInstance: boolean = false;
    public connected: boolean = false;
    public socket: WebSocket | null = null;
    public remoteIdentity: Identifier | null = null;

    constructor(name: string) { this.name = name; this.Init(); }

    async Init() {
        this.registerCallback('MetadataKeyValue', (MetaDataCallback: any) => {
                if (MetaDataCallback && MetaDataCallback.Key && MetaDataCallback.Value) {
                    if (this.remoteIdentity) {
                        this.remoteIdentity.Metadata[MetaDataCallback.Key] = MetaDataCallback.Value;
                    }
                    if (this.options.logging) console.log(`[${this.name}] Metadata updated: ${MetaDataCallback.Key} = ${MetaDataCallback.Value}`);
                }
        });
    }

    on(event: string, handler: EventHandler): void {
        if (!this.events[event]) this.events[event] = [];
        this.events[event].push(handler);
    }
    off(event: string, handler: EventHandler): void {
        if (!this.events[event]) return;
        this.events[event] = this.events[event].filter(h => h !== handler);
    }
    _emit(event: string, ...args: any[]): void {
        if (this.events[event]) {
            this.events[event].forEach(h => h(...args));
        }
    }

    async connect(url: string): Promise<boolean> {
        return new Promise((resolve, reject) => {
            this.socket = new WebSocket(url);
            this.socket.binaryType = 'arraybuffer';
            this.socket.onopen = () => {
                this.connected = true;
                this._emit('connected', this);
                if (this.options.logging) console.log(`[${this.name}] Connected to WebSocket -> ${url}`);
                resolve(true);
            };
            this.socket.onerror = (err) => {
                this._emit('error', err);
                if (this.options.logging) console.error(`[${this.name}] WebSocket error`, err);
                reject(err);
            };
            this.socket.onclose = (e) => {
                this.connected = false;
                this._emit('disconnected', e);
                if (this.options.logging) console.log(`[${this.name}] Disconnected from WebSocket`);
            };
            this.socket.onmessage = (msg: MessageEvent) => {
                let data: any = msg.data;
                if (this.options.useCompression) {
                    if (data instanceof ArrayBuffer) {
                        try {
                            // @ts-ignore
                            let decompressed = pako.ungzip(new Uint8Array(data), { to: 'string' });
                            data = decompressed;
                        } catch (e) {
                            this._emit('error', e);
                            if (this.options.logging) console.error(`[${this.name}] Decompression error`, e);
                        }
                    }
                }
                let obj: any;
                try {
                    obj = typeof data === 'string' ? JSON.parse(data) : data;
                    this._handleReceive(obj);
                } catch (e) {
                    if (this.options.logging) console.error(`[${this.name}] Object deserialization error.`, e);
                }
            };
        });
    }

    _wrap(obj: any): string {
        return `{"Type":"JSType.${obj.constructor.name}", "Value":${JSON.stringify(obj)}}`;
    }

    send(obj: any): void {
        if (!this.connected || !this.socket) return;
        var wrapped = this._wrap(obj);
        if (this.options.useCompression) {
            // @ts-ignore
            if (typeof pako !== 'undefined') {
                try {
                    // @ts-ignore
                    var compressed = pako.gzip(wrapped);
                    this.socket.send(compressed.buffer);
                    this._emit('sent', obj);
                    if (this.options.logging) console.log(`[${this.name}] Sent compressed object of type '${obj.constructor.name}'`);
                    return;
                } catch (e) {
                    this._emit('error', e);
                    if (this.options.logging) console.error(`[${this.name}] Compression error`, e);
                }
            } else {
                this._emit('error', new Error('pako library not loaded'));
                if (this.options.logging) console.error(`[${this.name}] pako library not loaded`);
            }
        }
        this.socket.send(wrapped);
        this._emit('sent', obj);
        if (this.options.logging) console.log(`[${this.name}] Sent object of type '${obj.constructor.name}'`);
    }

    sendBroadcast(obj: any): void {
        for (let peer of this.peers.values()) {
            this.sendToPeer(peer, obj);
        }
    }

    sendToPeer(peer: any, obj: any): PeerRedirect {
        // Simulate peer-to-peer by sending a message with recipient info
        let peerRedirect = new PeerRedirect(peer.id, obj);
        let wrapped = this._wrap(peerRedirect);
        this.send(wrapped);
        return peerRedirect;
    }

    sendSegmented(obj: any): void {
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

    async startServer(id: string, options: object = {}, name: string = "WebSocketP2PServer", port: number = 0): Promise<ServerInfo | null> {
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

    async acceptPeerConnection(server: ServerInfo, name: string = "WebSocketP2PClient"): Promise<WebSocketClient | null> {
        if (!this.options.usePeerToPeer) {
            this._emit('error', new Error('P2P is not enabled.'));
            return null;
        }
        let newClient: WebSocketClient = new WebSocketClient(name);
        this.p2pClients.set(server.id, newClient);
        newClient.name = name;
        try {
            await newClient.connect(`ws://${server.id}:${server.port}`);
            return newClient;
        } catch (err) {
            this._emit('error', err);
            return null;
        }
    }

    _handleReceive(obj: any): void {
        if (obj && obj.type === 'Identifier') {
            this._handleIdentifier(obj);
        } else {
            this._emit('receive', obj);
        }
        this.invokeAllCallbacks(obj);
    }

    registerCallback(type: string, callback: Callback): void {
        if (!this.typeCallbacks.has(type)) {
            this.typeCallbacks.set(type, []);
        }
        this.typeCallbacks.get(type)!.push(callback);
    }

    removeCallback(type: string, callback: Callback): void {
        if (!this.typeCallbacks.has(type)) return;
        const arr = this.typeCallbacks.get(type)!.filter(cb => cb !== callback);
        if (arr.length > 0) {
            this.typeCallbacks.set(type, arr);
        } else {
            this.typeCallbacks.delete(type);
        }
    }

    invokeAllCallbacks(obj: any): void {
        const type = obj.constructor.name;
        if (type && this.typeCallbacks.has(type)) {
            this.typeCallbacks.get(type)!.forEach(cb => cb(obj));
        }
    }

    _handleIdentifier(pUpdate: Identifier): void {
        switch (pUpdate.Action) {
            case PeerAction.LocalIdentity:
                this.remoteIdentity = Identifier.Create(
                    pUpdate.ID || '',
                    pUpdate.IP || '',
                    pUpdate.Metadata || {},
                    pUpdate.PrivateMetadata,
                    this
                );
                this.remoteIdentity.Action = PeerAction.LocalIdentity;
                this._emit('identified', this.remoteIdentity);
                if (this.options.logging) {
                    console.log(`[${this.name}] Local identity set: ID=${this.remoteIdentity.ID}`);
                }
                break;
            case PeerAction.RemoteIdentity:
                const remotePeer = Identifier.Create(
                    pUpdate.ID || '',
                    pUpdate.IP || '',
                    pUpdate.Metadata || {},
                    pUpdate.PrivateMetadata,
                    this
                );
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
                const metaPeer = Identifier.Create(
                    pUpdate.ID || '',
                    pUpdate.IP || '',
                    pUpdate.Metadata || {},
                    pUpdate.PrivateMetadata,
                    this
                );
                metaPeer.Action = PeerAction.MetadataUpdate;
                this.peers.set(pUpdate.ID, metaPeer);
                this._emit('peerMetaDataChanged', metaPeer);
                this._emit('peerUpdate', pUpdate);
                break;
            default:
                break;
        }
    }

    dispose(): void {
        if (this.connected && this.socket) {
            this.socket.close();
            this.isDisposed = true;
            this._emit('disposing');
        }
    }

    _generateGuid(): string {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            var r = Math.random() * 16 | 0, v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }
}

if (typeof window !== 'undefined') {
    (window as any).WebSocketClient = WebSocketClient;
}
