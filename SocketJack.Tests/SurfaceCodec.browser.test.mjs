import assert from "node:assert/strict";
import { test } from "node:test";
import { decodeSurfaceTile, SurfaceTileDecoder } from "../SocketJack/html/SurfaceCodec.mjs";

// Produced by the .NET encoder from a single BGRA pixel, then a zeroed 32x32 tile after resize.
const pixel = Buffer.from("U0JDAQEqAAAIU0IBCSUBAQABAQYBCAwBBAQBAQkAAwEC/w==", "base64");
const resized = Buffer.from("U0JDAQGnIAAACVNCAQmhIAECAAEBBgABAQEFBAAEAAAAIAEEBwEBiCA=", "base64");

test("decodes .NET packets and rejects duplicate and previous-generation tiles", () => {
    const decoder = new SurfaceTileDecoder();
    const tile = decoder.apply(pixel);
    assert.equal(tile.width, 1);
    assert.deepEqual([...tile.pixels], [0, 1, 2, 255]);
    assert.equal(decoder.apply(pixel), null);
    const next = decoder.apply(resized);
    assert.equal(next.generation, tile.generation + 1);
    assert.equal(next.pixels.length, 4096);
    assert.ok(next.pixels.every(value => value === 0));
    assert.equal(decoder.apply(pixel), null);
    decoder.reset();
    assert.equal(decoder.apply(pixel).generation, 1);
});

test("rejects truncated fragments, oversized expansion, and dictionary-dependent frames", () => {
    assert.throws(() => decodeSurfaceTile(pixel.subarray(0, pixel.length - 1)));
    assert.throws(() => decodeSurfaceTile(Uint8Array.of(83, 66, 67, 1, 1, 255, 127, 0)));
    assert.throws(() => decodeSurfaceTile(Uint8Array.of(83, 66, 67, 1, 1, 42, 1)));
    assert.throws(() => decodeSurfaceTile(new Uint8Array(8193)));
});
