"""Check 100 concurrent forwarding sessions with traffic and API polling, using temporary data."""
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time

from smoke_test import Client, EchoBackend, free_port, handshake, login_start, receive

ROOT = Path(__file__).resolve().parent.parent


def verify():
    with tempfile.TemporaryDirectory(prefix="proxycraft-capacity-") as temporary:
        directory = Path(temporary)
        web_port, game_port = free_port(), free_port()
        client = Client(f"http://127.0.0.1:{web_port}")
        backend = EchoBackend("capacity-backend").start()
        log = (directory / "app.log").open("w")
        app = subprocess.Popen(
            ["dotnet", str(ROOT / "bin/Debug/net10.0/ProxyCraft.dll"), "--urls", client.base_url],
            cwd=ROOT, stdout=log, stderr=subprocess.STDOUT,
            env={**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "Storage__Directory": str(directory / "data"),
                 "Proxy__ListenPort": str(game_port)},
        )
        sessions = []
        sessions_lock = threading.Lock()
        stop = threading.Event()
        samples = []

        def sample():
            while not stop.is_set():
                try:
                    status = dict(line.split(":", 1) for line in Path(f"/proc/{app.pid}/status").read_text().splitlines() if ":" in line)
                    samples.append({"rssKiB": int(status["VmRSS"].split()[0]), "threads": int(status["Threads"]),
                                    "fileDescriptors": len(list(Path(f"/proc/{app.pid}/fd").iterdir()))})
                except (OSError, KeyError):
                    pass
                stop.wait(0.1)

        sampler = threading.Thread(target=sample, daemon=True)
        try:
            deadline = time.monotonic() + 20
            while True:
                try:
                    client.session()
                    break
                except OSError:
                    assert app.poll() is None, (directory / "app.log").read_text()
                    assert time.monotonic() < deadline, "App startup timed out"
                    time.sleep(0.1)
            status, route, _ = client.request("/api/v1/proxies", "POST", {
                "domain": "capacity.example.test", "destinationHost": "127.0.0.1", "destinationPort": backend.port,
            })
            assert status == 201, route
            path = f'/api/v1/proxies/{route["id"]}'
            sampler.start()
            time.sleep(0.2)
            baseline = samples[-1] if samples else None

            def connect(index):
                connection = socket.create_connection(("127.0.0.1", game_port), timeout=20)
                with sessions_lock:
                    sessions.append(connection)
                packet = handshake(route["domain"]) + login_start(f"Capacity{index:03}")
                connection.sendall(packet)
                assert receive(connection, len(backend.label)) == backend.label
                assert receive(connection, len(packet)) == packet
                return connection

            started = time.monotonic()
            with ThreadPoolExecutor(max_workers=100) as workers:
                connections = list(workers.map(connect, range(100)))
            opened_seconds = time.monotonic() - started
            status, entries, _ = client.request(path + "/connections")
            assert status == 200 and len(entries) == 100
            assert next(proxy for proxy in client.request('/api/v1/proxies')[1] if proxy['id'] == route['id'])['liveConnectionCount'] == 100
            assert len({entry["id"] for entry in entries}) == 100
            assert {entry["playerName"] for entry in entries} == {f"Capacity{index:03}" for index in range(100)}
            history = client.request(path + "/events")[1]
            assert len(history) == 20
            assert [entry["id"] for entry in history] == sorted((entry["id"] for entry in history), reverse=True)
            print(f"PASS: 100 simultaneous login attempts opened 100 distinct forwarding sessions in {opened_seconds:.2f}s; saved history stays at 20.", flush=True)

            traffic_stop = threading.Event()

            def traffic(connection):
                payload = b"capacity-forwarding" * 512
                rounds = 0
                while not traffic_stop.is_set():
                    connection.sendall(payload)
                    assert receive(connection, len(payload)) == payload
                    rounds += 1
                    traffic_stop.wait(0.02)
                return rounds * len(payload) * 2

            latencies = []
            with ThreadPoolExecutor(max_workers=100) as workers:
                transfers = [workers.submit(traffic, connection) for connection in connections]
                try:
                    for attempt in range(5):
                        requested = time.monotonic()
                        status, current, _ = client.request(path + "/connections")
                        latencies.append((time.monotonic() - requested) * 1000)
                        assert status == 200 and current == entries
                        assert next(proxy for proxy in client.request('/api/v1/proxies')[1] if proxy['id'] == route['id'])['liveConnectionCount'] == 100
                        assert len(client.request(path + "/events")[1]) == 20
                        assert client.request("/api/v1/system/metrics")[0] == 200
                        assert app.poll() is None
                        time.sleep(0.5)
                finally:
                    traffic_stop.set()
                transferred = sum(future.result() for future in transfers)
            for connection in sessions:
                connection.close()
            deadline = time.monotonic() + 10
            while client.request(path + "/connections")[1]:
                assert time.monotonic() < deadline, "Closed sessions remained in memory"
                time.sleep(0.05)
            assert client.request(path + "/events")[1] == history
            assert next(proxy for proxy in client.request('/api/v1/proxies')[1] if proxy['id'] == route['id'])['liveConnectionCount'] == 0
            stop.set()
            sampler.join(timeout=2)
            result = {"connections": 100, "openSeconds": round(opened_seconds, 2),
                      "bidirectionalMiB": round(transferred / 1024 / 1024, 2), "liveApiMaxMs": round(max(latencies), 2),
                      "baseline": baseline, "peak": {key: max(sample[key] for sample in samples) for key in samples[0]} if samples else None}
            print("PASS: all sessions relay verified bidirectional traffic while the live/history/metrics APIs respond; closing them clears the live list without changing history.", flush=True)
            print(json.dumps(result, indent=2), flush=True)
        finally:
            stop.set()
            if sampler.is_alive():
                sampler.join(timeout=2)
            for connection in sessions:
                connection.close()
            app.terminate()
            try:
                app.wait(timeout=15)
            except subprocess.TimeoutExpired:
                app.kill()
                app.wait()
            backend.stop()
            log.close()


if __name__ == "__main__":
    verify()
