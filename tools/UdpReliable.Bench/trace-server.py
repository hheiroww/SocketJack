"""Capture a server thread-stack sample trace; timings are diagnostic, not acceptance results."""
import json
from pathlib import Path
import socket
import subprocess

root = Path(__file__).resolve().parents[2]
directory = root / "artifacts/udp-reliable/diagnosis"
binary = root / "tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll"
tool = root / "artifacts/udp-reliable/tools/dotnet-trace.exe"
with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocation:
    allocation.bind(("127.0.0.1", 0))
    port = allocation.getsockname()[1]
command = [str(tool), "collect", "--profile", "dotnet-sampled-thread-time", "--format", "Speedscope",
    "--output", str(directory / "server.nettrace"), "--show-child-io", "--",
    "dotnet", str(binary), "server", "fast-lan-32k", str(port)]
flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
trace = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=flags)
lines = []
try:
    while True:
        line = trace.stdout.readline()
        if not line:
            raise RuntimeError(trace.stderr.read())
        lines.append(line)
        if "READY" in line:
            break
    client = subprocess.run(["dotnet", str(binary), "client", "fast-lan-32k", "127.0.0.1", str(port), "0", "256"],
        capture_output=True, text=True, timeout=120, creationflags=flags)
    if client.returncode:
        raise RuntimeError(client.stderr)
    (directory / "sampled-server-measurement.json").write_text(client.stdout)
    stdout, stderr = trace.communicate(timeout=60)
    (directory / "server-trace.log").write_text("".join(lines) + stdout + stderr)
    if trace.returncode:
        raise RuntimeError(stderr)
    print(client.stdout)
finally:
    if trace.poll() is None:
        trace.kill()
        trace.wait()
