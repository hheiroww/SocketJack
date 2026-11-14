const tileSize = 32;
const maximumDimension = 4096;
const maximumPixels = 8294400;
const maximumPacketBytes = 8192;
const tileHeaderBytes = 33;
const maximumSerializedBytes = tileHeaderBytes + tileSize * tileSize * 4 + 6;

function version(bytes, expected) {
    return expected.every((value, index) => bytes[index] === value);
}

function reader(bytes) {
    let position = 0;
    const read = count => {
        if (count < 0 || count > bytes.length - position) {
            throw new RangeError("Truncated surface packet");
        }
        const result = bytes.subarray(position, position + count);
        position += count;
        return result;
    };
    const byte = () => read(1)[0];
    const number = () => {
        let result = 0;
        for (let shift = 0; shift < 35; shift += 7) {
            const current = byte();
            result += (current & 127) * 2 ** shift;
            if (current < 128 && result <= maximumPacketBytes) return result;
            if (result > maximumPacketBytes) break;
        }
        throw new RangeError("Surface packet length exceeds its limit");
    };
    return { read, byte, number, done: () => position === bytes.length };
}

function expand(packet) {
    if (!version(packet, [83, 66, 67, 1])) return packet;
    const input = reader(packet.subarray(4));
    if (input.byte() !== 1) throw new Error("Expected a compressed surface tile");
    const length = input.number();
    if (length > maximumSerializedBytes || input.number() !== 0) {
        throw new RangeError("Surface expansion exceeds its limit or contains a dictionary");
    }
    const output = new Uint8Array(length);
    let written = 0;
    while (written < length) {
        const token = input.byte();
        if (token > 1) throw new Error("Unsupported surface compression token");
        const distance = token === 1 ? input.number() : 0;
        const count = input.number();
        if (!count || count > length - written || (token === 1 && (!distance || distance > written))) {
            throw new RangeError("Invalid surface compression range");
        }
        if (token === 0) output.set(input.read(count), written);
        else {
            for (let index = written; index < written + count; index++) {
                output[index] = output[index - distance];
            }
        }
        written += count;
    }
    if (!input.done()) throw new Error("Trailing surface compression data");
    return output;
}

export function decodeSurfaceTile(packet) {
    if (!(packet instanceof Uint8Array) || packet.length > maximumPacketBytes) {
        throw new RangeError("Invalid surface packet");
    }
    const serialized = expand(packet);
    if (serialized.length > maximumSerializedBytes || !version(serialized, [83, 66, 1, 9])) {
        throw new Error("Expected an SB1 byte array");
    }
    const input = reader(serialized.subarray(4));
    const bytes = input.read(input.number());
    if (!input.done() || bytes.length < tileHeaderBytes || bytes[0] !== 1) {
        throw new Error("Unsupported or invalid surface tile version");
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const generation = Number(view.getBigInt64(1, true));
    const sequence = Number(view.getBigInt64(9, true));
    const width = view.getInt32(17, true);
    const height = view.getInt32(21, true);
    const x = view.getInt32(25, true);
    const y = view.getInt32(29, true);
    if (width < 1 || height < 1 || width > maximumDimension || height > maximumDimension ||
        width * height > maximumPixels || x < 0 || y < 0 || x >= width || y >= height ||
        x % tileSize || y % tileSize || generation < 1 || sequence < 1 ||
        !Number.isSafeInteger(generation) || !Number.isSafeInteger(sequence)) {
        throw new RangeError("Invalid surface dimensions, coordinates or sequence");
    }
    const tileWidth = Math.min(tileSize, width - x);
    const tileHeight = Math.min(tileSize, height - y);
    const pixels = bytes.slice(tileHeaderBytes);
    if (pixels.length !== tileWidth * tileHeight * 4) throw new RangeError("Invalid surface payload length");
    return { width, height, x, y, generation, sequence, tileWidth, tileHeight, pixels };
}

export class SurfaceTileDecoder {
    #generation = 0;
    #width = 0;
    #height = 0;
    #latest = new Map();

    reset() {
        this.#generation = this.#width = this.#height = 0;
        this.#latest.clear();
    }

    apply(packet) {
        const tile = decodeSurfaceTile(packet);
        if (tile.generation < this.#generation) return null;
        if (tile.generation > this.#generation) {
            this.#generation = tile.generation;
            this.#width = tile.width;
            this.#height = tile.height;
            this.#latest.clear();
        }
        if (tile.width !== this.#width || tile.height !== this.#height) {
            throw new Error("Surface dimensions changed without a new generation");
        }
        const index = tile.y * tile.width + tile.x;
        if ((this.#latest.get(index) ?? 0) >= tile.sequence) return null;
        this.#latest.set(index, tile.sequence);
        return tile;
    }
}
