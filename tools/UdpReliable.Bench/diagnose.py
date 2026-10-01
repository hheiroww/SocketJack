"""Causal diagnostics, NOT speed acceptance. Build isolated variants as described in the report."""
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
output = root / "artifacts/udp-reliable/diagnosis"
variants = {
    "baseline": root / "tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll",
    "wake-coalesced": output / "wake-coalesced/bin/Release/net8.0/UdpReliable.Bench.dll",
    "wake-two-flight": output / "wake-two-flight/bin/Release/net8.0/UdpReliable.Bench.dll",
}
timed = "--timed" in sys.argv
if timed:
    variants = {"timed": output / "timed/bin/Release/net8.0/UdpReliable.Bench.dll"}
record_path = output / ("timed-diagnostics.json" if timed else "ab-diagnostics.json")
rows = []
if "--resume" in sys.argv:
    rows = json.loads(record_path.read_text())["Measurements"]

def measure(variant, mode, run, profile=False):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocation:
        allocation.bind(("127.0.0.1", 0))
        port = allocation.getsockname()[1]
    env = os.environ.copy()
    env.pop("SJ_DIAGNOSTICS", None)
    env.pop("SJ_TRANSPORT_PROFILE", None)
    if timed:
        env["SJ_TRANSPORT_PROFILE"] = str(output / "timings")
    if profile:
        env["SJ_DIAGNOSTICS"] = str(output / ("exceptions-" + variant))
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    server = subprocess.Popen(["dotnet", str(variants[variant]), "server", mode, str(port)],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env, creationflags=flags)
    try:
        if server.stdout.readline().strip() != "READY":
            raise RuntimeError(server.stderr.read())
        client = subprocess.run(["dotnet", str(variants[variant]), "client", mode, "127.0.0.1", str(port), str(run), "256"],
            capture_output=True, text=True, env=env, timeout=120, creationflags=flags)
        if client.returncode:
            raise RuntimeError(client.stderr)
        row = json.loads(client.stdout)
        row.update(Variant=variant, ExceptionDiagnostics=profile)
        rows.append(row)
        record_path.write_text(json.dumps({
            "Purpose": "Diagnostic ablation; not a ten-round TCP acceptance comparison",
            "Network": "Two processes on clean IPv4 loopback; 32 KiB buffers; same serializer and workload",
            "Binaries": {v: hashlib.sha256(p.with_name("SocketJack.dll").read_bytes()).hexdigest() for v, p in variants.items()},
            "Measurements": rows}, indent=2))
        print(f"{variant} {mode} run={run} profile={profile}: {row['MiBPerSecond']:.2f} MiB/s; p95={row['P95Milliseconds']:.4f} ms; retries={row['Retransmissions']}", flush=True)
        server.wait(timeout=15)
        if server.returncode:
            raise RuntimeError(server.stderr.read())
    finally:
        if server.poll() is None:
            server.kill()
            server.wait()
        server.stdout.close()
        server.stderr.close()

output.mkdir(parents=True, exist_ok=True)
if timed:
    for mode in ("fast-lan", "fast-lan-32k"):
        measure("timed", mode, 0)
    sys.exit(0)
hashes = [hashlib.sha256(p.with_name("SocketJack.dll").read_bytes()).hexdigest() for p in variants.values()]
if len(set(hashes)) != len(hashes):
    raise RuntimeError("Variants must have distinct library binaries. Check isolated intermediate paths before measuring.")
if "--clean-only" not in sys.argv:
    for variant in ("wake-coalesced", "wake-two-flight"):
        for mode in ("fast-lan", "fast-lan-32k"):
            measure(variant, mode, -1, profile=True)

names = list(variants)
for run in range(3):
    modes = ("fast-lan", "fast-lan-32k") if run % 2 == 0 else ("fast-lan-32k", "fast-lan")
    for mode in modes:
        for variant in names[run:] + names[:run]:
            if any(x["Variant"] == variant and x["Mode"] == mode and x["Run"] == run and not x["ExceptionDiagnostics"] for x in rows):
                continue
            measure(variant, mode, run)
