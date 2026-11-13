const protocol = "socketjack.application.v1";
const maximumPending = 16;

export class SocketJackApplicationClient {
    #url;
    #socket;
    #connecting;
    #pending = new Map();
    #binaryReply;
    #token;

    constructor(endpoint) {
        this.#url = new URL(endpoint, globalThis.location?.href);
        if (this.#url.protocol === "https:") this.#url.protocol = "wss:";
        if (this.#url.protocol === "http:") this.#url.protocol = "ws:";
        const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(this.#url.hostname);
        if ((this.#url.protocol !== "wss:" && !(this.#url.protocol === "ws:" && loopback)) ||
            this.#url.username || this.#url.password || this.#url.hash)
            throw new TypeError("Use a secure WebSocket endpoint or loopback development endpoint.");
    }

    async invoke(operation, input, signal, binary = false) {
        if (signal?.aborted) throw signal.reason;
        const socket = await this.#connect();
        return this.#request(socket, operation, input, signal, binary);
    }

    async appendMedia(id, offset, sha256, bytes, signal) {
        if (!(bytes instanceof ArrayBuffer) || bytes.byteLength < 1 || bytes.byteLength > 65536)
            throw new TypeError("Invalid media chunk.");
        const socket = await this.#connect();
        return this.#request(socket, "media.appendBinary", { id, offset, sha256, length: bytes.byteLength }, signal, false, bytes);
    }

    close() {
        this.#token = undefined;
        this.#socket?.close();
    }

    #connect() {
        if (this.#connecting) return this.#connecting;
        if (this.#socket?.readyState === WebSocket.OPEN) return Promise.resolve(this.#socket);
        const socket = new WebSocket(this.#url, protocol);
        socket.binaryType = "arraybuffer";
        this.#socket = socket;
        this.#connecting = new Promise((resolve, reject) => {
            socket.addEventListener("open", async () => {
                try {
                    if (this.#token) await this.#request(socket, "session.resume", { token: this.#token });
                    resolve(socket);
                } catch (error) {
                    this.#token = undefined;
                    socket.close();
                    reject(error);
                }
            }, { once: true });
            socket.addEventListener("close", () => {
                const error = new Error("SocketJack connection closed.");
                reject(error);
                if (this.#socket !== socket) return;
                for (const pending of this.#pending.values()) pending.reject(error);
                this.#pending.clear();
                this.#binaryReply?.pending.reject(error);
                this.#binaryReply = undefined;
            }, { once: true });
            socket.addEventListener("message", event => {
                if (this.#socket === socket) this.#message(socket, event.data);
            });
        }).finally(() => { this.#connecting = undefined; });
        return this.#connecting;
    }

    #request(socket, operation, input, signal, binary = false, upload) {
        if (signal?.aborted) throw signal.reason;
        if (this.#pending.size >= maximumPending || socket.bufferedAmount > 256 * 1024)
            throw new Error("SocketJack request queue is full.");
        const id = crypto.randomUUID();
        return new Promise((resolve, reject) => {
            const abort = () => { reject(signal.reason); socket.close(); };
            const finish = () => signal?.removeEventListener("abort", abort);
            this.#pending.set(id, {
                binary,
                resolve: value => {
                    finish();
                    if (operation === "session.signIn") this.#token = value.token;
                    if (operation === "session.signOut") this.#token = undefined;
                    resolve(value);
                },
                reject: error => { finish(); reject(error); }
            });
            signal?.addEventListener("abort", abort, { once: true });
            try {
                socket.send(JSON.stringify({ id, operation, input }));
                if (upload) socket.send(upload);
            }
            catch (error) {
                finish();
                this.#pending.delete(id);
                reject(error);
            }
        });
    }

    #message(socket, data) {
        try {
            if (this.#binaryReply) {
                const { pending, metadata } = this.#binaryReply;
                if (!(data instanceof ArrayBuffer) || data.byteLength !== metadata.byteLength)
                    throw new Error("Invalid SocketJack binary response.");
                this.#binaryReply = undefined;
                pending.resolve({ metadata, bytes: data });
                return;
            }
            const message = JSON.parse(data);
            const pending = this.#pending.get(message.id);
            if (!pending) throw new Error("Unknown SocketJack request ID.");
            this.#pending.delete(message.id);
            if (!message.ok) {
                pending.reject(Object.assign(new Error(message.error?.message), { code: message.error?.code }));
            } else if (pending.binary) {
                if (message.value.byteLength === 0) pending.resolve({ metadata: message.value, bytes: new ArrayBuffer(0) });
                else this.#binaryReply = { pending, metadata: message.value };
            } else {
                pending.resolve(message.value);
            }
        } catch (error) {
            this.#binaryReply?.pending.reject(error);
            socket.close();
        }
    }
}
