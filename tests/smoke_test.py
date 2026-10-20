"""Exercise the real app with temporary storage; requires a Debug build and Python 3."""
import http.cookiejar
import html
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, closing
import json
import os
import re
from pathlib import Path
import socket
import socketserver
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent


def free_port():
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        return reservation.getsockname()[1]


class EchoBackend:
    """A real TCP backend identifying itself before echoing the unmodified byte stream."""
    def __init__(self, label):
        self.label = label.encode()
        self.port = 0
        self.server = None
        self.connections = set()
        self.connection_lock = threading.Lock()

    def start(self):
        label = self.label
        backend = self

        class Handler(socketserver.BaseRequestHandler):
            def handle(self):
                with backend.connection_lock:
                    backend.connections.add(self.request)
                try:
                    self.request.sendall(label)
                    while data := self.request.recv(8192):
                        self.request.sendall(data)
                except OSError:
                    pass
                finally:
                    with backend.connection_lock:
                        backend.connections.discard(self.request)

        class Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = True
            request_queue_size = 128

        self.server = Server(("127.0.0.1", self.port), Handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        return self

    def disconnect_clients(self):
        with self.connection_lock:
            connections = list(self.connections)
        for connection in connections:
            try:
                connection.shutdown(socket.SHUT_RDWR)
                connection.close()
            except OSError:
                pass

    def stop(self):
        if self.server:
            self.server.shutdown()
            self.server.server_close()
            self.thread.join(timeout=2)
            self.server = None


def receive(connection, length):
    result = b""
    while len(result) < length:
        part = connection.recv(length - len(result))
        assert part, "The proxy disconnected unexpectedly."
        result += part
    return result


def varint(value):
    value &= 0xffffffff
    encoded = b""
    while value >= 128:
        encoded += bytes([(value & 127) | 128])
        value >>= 7
    return encoded + bytes([value])


def handshake(domain, state=2):
    address = domain.encode()
    packet = b"\x00" + varint(-1 if state == 1 else 767) + varint(len(address)) + address + b"\x63\xdd" + varint(state)
    return varint(len(packet)) + packet


def login_start(player_name="TestPlayer", profile=b"\x00" * 16):
    name = player_name.encode()
    packet = b"\x00" + varint(len(name)) + name + profile
    return varint(len(packet)) + packet


def connect_route(port, domain, backend, state=2, fragmented=False, player_name="TestPlayer", profile=b"\x00" * 16):
    connection = socket.create_connection(("127.0.0.1", port), timeout=3)
    packet = handshake(domain, state)
    if state in (2, 3):
        packet += login_start(player_name, profile)
    if fragmented:
        for byte in packet:
            connection.sendall(bytes([byte]))
            time.sleep(0.001)
    else:
        connection.sendall(packet)
    assert receive(connection, len(backend.label)) == backend.label
    assert receive(connection, len(packet)) == packet, "Original handshake/login bytes must reach the selected backend unchanged."
    return connection


def assert_closed(connection):
    try:
        assert connection.recv(1) == b"", "The old route still accepted traffic."
    except (ConnectionResetError, ConnectionAbortedError):
        pass


def exchange(connection, message):
    connection.sendall(message)
    assert receive(connection, len(message)) == message


def wait_health(client, route_id, reachable):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        routes = client.request("/api/v1/proxies")[1]
        route = next(route for route in routes if route["id"] == route_id)
        if route["isReachable"] is reachable:
            assert route["lastCheckedAt"]
            return
        time.sleep(0.1)
    raise AssertionError(f"Route {route_id} never became reachable={reachable}: {route}")


def verify_forwarding(client, first, second, backends, listener_port, database):
    backend_a, backend_b, backend_c = backends
    original = {"domain": first["domain"], "destinationHost": "127.0.0.1", "destinationPort": backend_a.port}
    other = {"domain": second["domain"], "destinationHost": "127.0.0.1", "destinationPort": backend_b.port}
    first_path = f'/api/v1/proxies/{first["id"]}'
    assert client.request(first_path, "PUT", original)[0] == 200
    assert client.request(f'/api/v1/proxies/{second["id"]}', "PUT", other)[0] == 200
    wait_health(client, first["id"], True)
    with closing(database()) as connection:
        created_at = connection.execute("SELECT created_at FROM proxies WHERE Id=?", (first["id"],)).fetchone()[0]

    with connect_route(listener_port, first["domain"].upper() + ".\0FML\0", backend_a, fragmented=True) as first_connection, \
            connect_route(listener_port, second["domain"], backend_b) as second_connection:
        exchange(first_connection, b"first route before edit")
        exchange(second_connection, b"other route before edit")
        assert client.request(first_path, "PUT", {**original, "domain": second["domain"]})[0] == 409
        assert client.request(first_path, "PUT", {**other, "domain": first["domain"]})[0] == 409
        assert client.request(first_path, "PUT", {**original, "destinationPort": 0})[0] == 400
        assert client.request(first_path, "PUT", {**original, "domain": "https://invalid.test"})[0] == 400
        assert client.request(first_path, "PUT", {**original, "domain": "servername"})[0] == 400
        assert client.request(first_path, "PUT", original, csrf=False)[0] == 400
        assert client.request("/api/v1/proxies/2147483647", "PUT", original)[0] == 404
        exchange(first_connection, b"failed saves leave sessions connected")
        updated = {**original, "domain": "NEW.Example.test.", "destinationPort": backend_c.port}
        status, saved, _ = client.request(first_path, "PUT", updated)
        assert status == 200 and saved["domain"] == "new.example.test", (status, saved)
        assert_closed(first_connection)
        exchange(second_connection, b"other route survives edit")
        with socket.create_connection(("127.0.0.1", listener_port), timeout=3) as removed:
            removed.sendall(handshake(first["domain"]))
            assert_closed(removed)
        with connect_route(listener_port, "new.example.test", backend_c, state=1) as replacement:
            exchange(replacement, b"new destination works immediately")
            # Saving the same settings also restarts only this route.
            assert client.request(first_path, "PUT", updated)[0] == 200
            assert_closed(replacement)
        exchange(second_connection, b"other route survives repeated saves")

    for invalid in (b"\xff\xff\xff\xff\xff\xff", b"\xff\x7f", b"\x01\x01", handshake("unknown.example.test")):
        with socket.create_connection(("127.0.0.1", listener_port), timeout=3) as connection:
            connection.sendall(invalid)
            assert_closed(connection)
    with connect_route(listener_port, "new.example.test", backend_c, state=3) as transfer:
        exchange(transfer, bytes(range(256)) * 512)
    with closing(database()) as connection:
        row = connection.execute("SELECT Domain, DestinationPort, created_at, modified_at FROM proxies WHERE Id=?", (first["id"],)).fetchone()
        assert row == ("new.example.test", backend_c.port, created_at, row[3]) and row[3]
    backend_c.stop()
    wait_health(client, first["id"], False)
    with socket.create_connection(("127.0.0.1", listener_port), timeout=3) as unavailable:
        unavailable.sendall(handshake("new.example.test") + login_start())
        assert_closed(unavailable)
    with connect_route(listener_port, second["domain"], backend_b) as unaffected:
        exchange(unaffected, b"unavailable backend does not affect other routes")
    backend_c.start()
    wait_health(client, first["id"], True)
    with connect_route(listener_port, "new.example.test", backend_c) as recovered:
        exchange(recovered, b"backend recovery needs no route toggle")
    print("PASS: real TCP forwarding, fragmented/status/login/transfer handshakes, isolated edits, validation, live health, recovery, and byte preservation.")


def verify_deletion(client, backends, listener_port, database, stop, start):
    routes = []
    for name, backend in zip(("delete", "keep"), backends):
        status, route, _ = client.request("/api/v1/proxies", "POST", {
            "domain": f"{name}.example.test", "destinationHost": "127.0.0.1", "destinationPort": backend.port,
        })
        assert status == 201, (status, route)
        routes.append(route)
    deleted, kept = routes
    path = f'/api/v1/proxies/{deleted["id"]}'
    with connect_route(listener_port, deleted["domain"], backends[0]) as removed_connection, \
            connect_route(listener_port, kept["domain"], backends[1]) as kept_connection:
        assert client.request(path, "DELETE", csrf=False)[0] == 400
        assert client.request("/api/v1/proxies/2147483647", "DELETE")[0] == 404
        exchange(removed_connection, b"rejected deletes preserve this route")
        assert client.request(path, "DELETE")[0] == 204
        assert_closed(removed_connection)
        exchange(kept_connection, b"other route survives deletion")
        with socket.create_connection(("127.0.0.1", listener_port), timeout=3) as unknown:
            unknown.sendall(handshake(deleted["domain"]))
            assert_closed(unknown)
        assert client.request(path, "DELETE")[0] == 404
        assert deleted["id"] not in [route["id"] for route in client.request("/api/v1/proxies")[1]]
        with closing(database()) as connection:
            assert connection.execute("SELECT Id FROM proxies WHERE Id=?", (deleted["id"],)).fetchone() is None
        # Both the domain and destination become available immediately after deletion.
        status, replacement, _ = client.request("/api/v1/proxies", "POST", {
            "domain": deleted["domain"], "destinationHost": "127.0.0.1", "destinationPort": backends[0].port,
        })
        assert status == 201
        with connect_route(listener_port, replacement["domain"], backends[0]) as restored:
            exchange(restored, b"deleted domain and destination can be reused")
        assert client.request(f'/api/v1/proxies/{replacement["id"]}', "DELETE")[0] == 204
        exchange(kept_connection, b"other route survives deletion again")
    stop()
    start()
    assert client.request("/api/v1/proxies?search=delete.example.test")[1] == []
    with connect_route(listener_port, kept["domain"], backends[1]) as restored:
        exchange(restored, b"unrelated saved route survives restart")
    assert client.request(f'/api/v1/proxies/{kept["id"]}', "DELETE")[0] == 204
    print("PASS: deletion persists, closes only its own sessions, rejects missing CSRF, releases the domain/destination, and stays deleted after restart.")


def verify_destination_uniqueness(client):
    first = {"domain": "atm10.example.test", "destinationHost": "192.168.120.40", "destinationPort": 25565}
    second = {**first, "domain": "other.example.test", "destinationPort": 25566}
    status, created, _ = client.request("/api/v1/proxies", "POST", first)
    assert status == 201
    status, other, _ = client.request("/api/v1/proxies", "POST", second)
    assert status == 201, "The same IP with a different port must be allowed."
    status, conflict, _ = client.request("/api/v1/proxies", "POST", {**first, "domain": "duplicate.example.test"})
    assert status == 409 and "destination host and port" in conflict["title"]
    assert client.request(f'/api/v1/proxies/{created["id"]}', "PUT", first)[0] == 200
    assert client.request(f'/api/v1/proxies/{other["id"]}', "PUT", {**second, "destinationPort": 25565})[0] == 409
    assert client.request("/api/v1/proxies", "POST", {**first, "domain": "mapped.example.test", "destinationHost": "[::ffff:192.168.120.40]"})[0] == 409
    routes = client.request("/api/v1/proxies")[1]
    assert len(routes) == 2 and next(route for route in routes if route["id"] == other["id"])["destinationPort"] == 25566

    ipv6 = {"domain": "ipv6-unique.example.test", "destinationHost": "[2001:0DB8:0:0:0:0:0:40]", "destinationPort": 25565}
    status, normalized, _ = client.request("/api/v1/proxies", "POST", ipv6)
    assert status == 201 and normalized["destinationHost"] == "2001:db8::40"
    assert client.request("/api/v1/proxies", "POST", {**ipv6, "domain": "ipv6-duplicate.example.test", "destinationHost": "2001:db8::40"})[0] == 409
    hostname = {**first, "domain": "hostname.example.test", "destinationHost": "Backend.Example.TEST."}
    status, normalized, _ = client.request("/api/v1/proxies", "POST", hostname)
    assert status == 201 and normalized["destinationHost"] == "backend.example.test"
    assert client.request("/api/v1/proxies", "POST", {**hostname, "domain": "hostname-duplicate.example.test", "destinationHost": "backend.example.test"})[0] == 409

    callers = [Client(client.base_url), Client(client.base_url)]
    for caller in callers:
        caller.session()

    def concurrently(operations):
        ready = threading.Barrier(2)

        def save(index):
            path, method, body = operations[index]
            ready.wait(timeout=5)
            return callers[index].request(path, method, body)[0]

        with ThreadPoolExecutor(max_workers=2) as pool:
            return sorted(pool.map(save, range(2)))

    shared = {**first, "destinationHost": "192.0.2.60"}
    results = concurrently([("/api/v1/proxies", "POST", {**shared, "domain": f"concurrent-{index}.example.test"}) for index in range(2)])
    assert results == [201, 409], results
    results = concurrently([
        (f'/api/v1/proxies/{created["id"]}', "PUT", {**first, "destinationHost": "192.0.2.70"}),
        (f'/api/v1/proxies/{other["id"]}', "PUT", {**second, "destinationHost": "192.0.2.70", "destinationPort": 25565}),
    ])
    assert results == [200, 409], results
    routes = client.request("/api/v1/proxies")[1]
    assert len([route for route in routes if route["destinationHost"] == "192.0.2.60"]) == 1
    assert len([route for route in routes if route["destinationHost"] == "192.0.2.70"]) == 1
    print("PASS: create/edit reject duplicate destinations, allow separate ports and unchanged edits, normalize IP/host spellings, and serialize concurrent saves.")


def verify_api_keys(client, database, stop, start, storage):
    status, reference, _ = client.request("/settings/api")
    assert status == 200 and "API reference" in reference and "SwaggerUIBundle" not in reference
    operations = re.findall(r'<details[^>]+data-api-method="([^"]+)" data-api-path="([^"]+)"[^>]*>(.*?)</details>', reference, re.S)
    assert len(operations) == 23 and all(path.startswith("/api/v1/") for method, path, body in operations)
    live = next(html.unescape(body) for method, path, body in operations if path == "/api/v1/proxies/{id}/connections")
    assert '"playerName": "Steve"' in live and '"connectedAt":' in live and "JSON array" in live and "404" in live
    assert '"createdAt"' not in live and '"destinationHost"' not in live
    history = next(html.unescape(body) for method, path, body in operations if path == "/api/v1/proxies/{id}/events")
    assert '"playerName": "Steve"' in history and '"createdAt":' in history and "JSON array" in history
    assert '"destinationHost"' not in history and "404" in history
    resources = next(html.unescape(body) for method, path, body in operations if path == "/api/v1/system/metrics")
    assert '"totalCpuCores": 4' in resources and '"memoryUsagePercent": 35.2' in resources
    create = next(html.unescape(body) for method, path, body in operations if method == "POST" and path == "/api/v1/proxies")
    assert "Create a proxy" in create and "201" in create and "409" in create
    assert '"destinationPort": 25565' in create and "Required." in create and "Defaults to 25565." in create
    listed = next(html.unescape(body) for method, path, body in operations if method == "GET" and path == "/api/v1/proxies")
    assert "JSON array" in listed and "search" in listed and "boolean | null" in listed
    assert '"liveConnectionCount": 3' in listed and "Current named forwarding sessions" in listed
    login = next(body for method, path, body in operations if path.endswith("/login"))
    assert "204" in login and "No response body." in login
    assert any(method == "DELETE" and path == "/api/v1/proxies/{id}" for method, path, body in operations)
    for removed in ("/api-docs/swagger-ui-bundle.js", "/api-docs/swagger-ui.css", "/api/openapi/v1.json"):
        assert client.request(removed)[0] == 404, removed
    assert client.request("/api/v1/settings/api-key")[1] == {"isConfigured": False, "keyPrefix": None}
    assert client.request("/api/v1/settings/api-key", "POST", csrf=False)[0] == 400
    status, generated, headers = client.request("/api/v1/settings/api-key", "POST")
    assert status == 200 and generated["apiKey"].startswith("pc_") and len(generated["apiKey"]) == 46
    assert "no-store" in headers["Cache-Control"]
    secret = generated["apiKey"]
    assert secret not in client.request("/settings/api")[1]
    with closing(database()) as connection:
        row = connection.execute("SELECT Id, KeyHash, KeyPrefix, created_at FROM api_keys").fetchone()
        assert row[0] == 1 and len(row[1]) == 64 and secret not in row and row[2] == generated["keyPrefix"] and row[3]

    external = Client(client.base_url)
    for path in ("/api/v1/proxies", "/api/v1/settings/app", "/api/v1/authentication/session", "/api/v1/system/metrics", "/api/v1/proxies/1/events", "/api/v1/proxies/1/connections"):
        assert external.request(path)[0] == 401, path
    assert external.request("/api/v1/proxies", extra_headers={"X-ProxyCraft-UI": "true"})[0] == 401
    assert external.request("/api/v1/proxies", extra_headers={"X-CSRF-TOKEN": "fake"})[0] == 401
    external.api_key = "invalid"
    assert external.request("/api/v1/proxies")[0] == 401
    external.api_key = secret
    assert external.request("/api/v1/proxies")[0] == 200
    assert external.request("/api/v1/system/metrics")[0] == 200
    payload = {"domain": "key.example.test", "destinationHost": "192.0.2.99", "destinationPort": 25565}
    status, route, _ = external.request("/api/v1/proxies", "POST", payload, csrf=False)
    assert status == 201, (status, route)
    assert external.request(f'/api/v1/proxies/{route["id"]}/events', csrf=False)[1] == []
    assert external.request(f'/api/v1/proxies/{route["id"]}/connections', csrf=False)[1] == []
    assert external.request(f'/api/v1/proxies/{route["id"]}', "PUT", {**payload, "domain": "invalid"}, csrf=False)[0] == 400
    assert external.request(f'/api/v1/proxies/{route["id"]}', "DELETE", csrf=False)[0] == 204
    assert external.request(f'/api/v1/proxies/{route["id"]}', "DELETE", csrf=False)[0] == 404
    # UI requests use the real cookie/token, including GET; it must keep working immediately after generation.
    assert client.request("/api/v1/proxies")[0] == 200
    status, metadata, _ = client.request("/api/v1/settings/api-key")
    assert status == 200 and metadata["isConfigured"] and "apiKey" not in metadata and "keyHash" not in metadata
    assert client.request("/api/v1/proxies", csrf=False)[0] == 401
    assert client.request("/api/v1/proxies", extra_headers={"X-API-Key": "invalid"})[0] == 401
    rotated = client.request("/api/v1/settings/api-key", "POST")[1]["apiKey"]
    assert rotated != secret and external.request("/api/v1/proxies")[0] == 401
    external.api_key = rotated
    assert external.request("/api/v1/proxies")[0] == 200
    with closing(database()) as connection:
        assert connection.execute("SELECT COUNT(*), modified_at FROM api_keys").fetchone()[0] == 1
        assert connection.execute("SELECT modified_at FROM api_keys").fetchone()[0]

    credentials = {"usernameOrEmail": "ApiOwner", "password": "test-only-api-password-123"}
    assert client.request("/api/v1/authentication/enable", "POST", credentials)[0] == 200
    assert client.request("/api/v1/proxies")[0] == 401
    assert external.request("/api/v1/proxies")[0] == 200, "A valid API key must authenticate without a browser login."
    assert client.request("/settings/api")[0] == 302, "The API reference must follow dashboard sign-in rules."
    # The login page provides an anonymous browser token; signing in redirects to a page with a new token.
    page = client.request("/account/login")[1]
    client.csrf = re.search(r'name="csrf-token" content="([^"]+)"', page)[1]
    assert client.request("/api/v1/authentication/login", "POST", credentials)[0] == 204
    page = client.request("/")[1]
    client.csrf = re.search(r'name="csrf-token" content="([^"]+)"', page)[1]
    assert client.session()["isAuthenticated"]
    assert client.request("/api/v1/proxies")[0] == 200
    assert client.request("/settings/api")[0] == 200
    stop()
    start()
    assert external.request("/api/v1/proxies")[0] == 200 and client.request("/api/v1/proxies")[0] == 200
    assert client.request("/api/v1/authentication/disable", "POST")[0] == 200
    page = client.request("/")[1]
    client.csrf = re.search(r'name="csrf-token" content="([^"]+)"', page)[1]
    # Simulate an older installation in this test's disposable database, then verify the narrow startup addition.
    before = client.request("/api/v1/proxies")[1]
    stop()
    with sqlite3.connect(storage / "proxycraft.db") as connection:
        connection.execute("DROP TABLE api_keys")
    start()
    assert len(client.request("/api/v1/proxies")[1]) == len(before)
    assert client.request("/api/v1/settings/api-key")[1]["isConfigured"] is False
    with closing(database()) as connection:
        assert connection.execute("SELECT COUNT(*) FROM users").fetchone()[0] == 1
    print("PASS: the custom API reference documents inputs/outputs, old documentation routes are gone, and API keys/auth/CSRF/rotation/persistence work.")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        return None


def verify_connection_events(client, backends, listener_port, database, stop, start, storage):
    routes = []
    for index in range(2):
        status, route, _ = client.request('/api/v1/proxies', 'POST', {
            'domain': f'events-{index}.example.test', 'destinationHost': '127.0.0.1', 'destinationPort': backends[index].port,
        })
        assert status == 201, route
        routes.append(route)
    first, second = routes
    path = f'/api/v1/proxies/{first["id"]}/events'
    second_path = f'/api/v1/proxies/{second["id"]}/events'
    assert client.request(path)[1] == [] and client.request(second_path)[1] == []
    assert client.request('/api/v1/proxies/2147483647/events')[0] == 404
    assert client.request(path, 'POST', {})[0] == 405, 'Events are created only by actual Minecraft login attempts.'

    with connect_route(listener_port, first['domain'], backends[0], state=1):
        pass
    for invalid in (b'\x02\x01\x00', b'\x05\x00\x03<x>', b'\x04\x00\x03ab', b'\xff\xff\xff\xff'):
        with socket.create_connection(('127.0.0.1', listener_port), timeout=3) as connection:
            connection.sendall(handshake(first['domain']) + invalid)
            connection.shutdown(socket.SHUT_WR)
            assert_closed(connection)
    assert client.request(path)[1] == [], 'Pings and invalid/incomplete login packets must not generate player events.'
    with socket.create_connection(('127.0.0.1', listener_port), timeout=7) as connection:
        connection.sendall(handshake(first['domain']))
        assert_closed(connection)
    assert client.request(path)[1] == [], 'A stalled login must time out without a fabricated player name.'

    for index in range(25):
        with connect_route(listener_port, first['domain'], backends[0], fragmented=index == 0, player_name=f'Player{index:02}'):
            pass
    with connect_route(listener_port, second['domain'], backends[1], player_name='Alex', profile=b''):
        pass
    entries = client.request(path)[1]
    assert [entry['playerName'] for entry in entries] == [f'Player{index:02}' for index in range(24, 4, -1)]
    assert all(entry['domain'] == first['domain'] and entry['createdAt'].endswith(('+00:00', 'Z')) for entry in entries)
    assert client.request(second_path)[1][0]['playerName'] == 'Alex'
    with closing(database()) as connection:
        assert connection.execute('SELECT COUNT(*) FROM connection_events WHERE ProxyId=?', (first['id'],)).fetchone()[0] == 20

    def attempt(index):
        with connect_route(listener_port, first['domain'], backends[0], player_name=f'Concurrent{index:02}') as connection:
            exchange(connection, b'login bytes and following traffic survive event recording')
        with closing(database()) as connection:
            assert connection.execute('SELECT COUNT(*) FROM connection_events WHERE ProxyId=?', (first['id'],)).fetchone()[0] <= 20

    with ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(attempt, range(32)))
    entries = client.request(path)[1]
    assert len(entries) == 20 and all(entry['playerName'].startswith('Concurrent') for entry in entries)
    assert [entry['id'] for entry in entries] == sorted((entry['id'] for entry in entries), reverse=True)
    assert client.request(second_path)[1][0]['playerName'] == 'Alex'
    print('PASS: real login names, fragmented/legacy profiles, no ping/invalid/stalled events, newest-first ordering, and atomic 20-per-proxy retention under concurrent logins.')

    backends[0].stop()
    try:
        with socket.create_connection(('127.0.0.1', listener_port), timeout=5) as connection:
            connection.sendall(handshake(first['domain']) + login_start('NoBackend'))
            assert_closed(connection)
        assert client.request(path)[1][0]['playerName'] == 'NoBackend'
    finally:
        backends[0].start()
    renamed = {**first, 'domain': 'renamed-events.example.test'}
    assert client.request(f'/api/v1/proxies/{first["id"]}', 'PUT', renamed)[0] == 200
    with connect_route(listener_port, renamed['domain'], backends[0], player_name='AfterRename', profile=bytes(range(256))):
        pass
    entries = client.request(path)[1]
    assert entries[0]['domain'] == renamed['domain'] and entries[1]['domain'] == first['domain']
    stop()
    start()
    assert client.request(path)[1] == entries, 'Connection events must persist across restarts.'

    with socket.create_connection(('127.0.0.1', listener_port), timeout=3) as pending:
        pending.sendall(handshake(renamed['domain']))
        assert client.request(f'/api/v1/proxies/{first["id"]}', 'DELETE')[0] == 204
        assert_closed(pending)
    assert client.request(path)[0] == 404
    with closing(database()) as connection:
        assert connection.execute('SELECT COUNT(*) FROM connection_events WHERE ProxyId=?', (first['id'],)).fetchone()[0] == 0
        assert connection.execute('SELECT COUNT(*) FROM connection_events WHERE ProxyId=?', (second['id'],)).fetchone()[0] == 1
    status, replacement, _ = client.request('/api/v1/proxies', 'POST', {
        'domain': renamed['domain'], 'destinationHost': '127.0.0.1', 'destinationPort': backends[0].port,
    })
    assert status == 201 and client.request(f'/api/v1/proxies/{replacement["id"]}/events')[1] == []
    assert client.request(f'/api/v1/proxies/{replacement["id"]}', 'DELETE')[0] == 204
    print('PASS: unavailable backends still record attempts; renamed domains retain the original event domain; history persists; deletion cascades only its own history and a recreated route starts empty.')

    def configurations():
        return [(proxy['id'], proxy['domain'], proxy['destinationHost'], proxy['destinationPort'], proxy['isActive'])
                for proxy in client.request('/api/v1/proxies')[1]]

    before = configurations()
    with sqlite3.connect(storage / 'proxycraft.db') as connection:
        existing = connection.execute("SELECT name, sql FROM sqlite_master WHERE type='table' AND name IN ('users','proxies','api_keys') ORDER BY name").fetchall()
        connection.execute('DROP TABLE connection_events')
    with connect_route(listener_port, second['domain'], backends[1], player_name='StorageFailure') as connection:
        exchange(connection, b'event storage failure must not interrupt Minecraft traffic')
    stop()
    start()
    assert configurations() == before
    with connect_route(listener_port, second['domain'], backends[1], player_name='NewTable'):
        pass
    assert client.request(second_path)[1][0]['playerName'] == 'NewTable'
    with closing(database()) as connection:
        assert connection.execute("SELECT name, sql FROM sqlite_master WHERE type='table' AND name IN ('users','proxies','api_keys') ORDER BY name").fetchall() == existing
        assert connection.execute('PRAGMA foreign_key_list(connection_events)').fetchone()[6] == 'CASCADE'
        row = connection.execute('SELECT created_at, modified_at, is_active FROM connection_events').fetchone()
        assert row[0] and row[1] is None and row[2] == 1
    assert client.request(f'/api/v1/proxies/{second["id"]}', 'DELETE')[0] == 204
    with closing(database()) as connection:
        assert connection.execute('SELECT COUNT(*) FROM connection_events WHERE ProxyId=?', (second['id'],)).fetchone()[0] == 0
    print('PASS: event-storage failure preserves forwarding; missing event table initializes automatically on older databases without altering existing schemas/routes; BaseModel fields and cascading deletion work.')


def verify_live_connections(client, backends, listener_port, database, stop, start):
    routes = []
    with closing(database()) as connection:
        schemas = connection.execute("SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name").fetchall()
    for index in range(2):
        status, route, _ = client.request('/api/v1/proxies', 'POST', {
            'domain': f'live-{index}.example.test', 'destinationHost': '127.0.0.1', 'destinationPort': backends[index].port,
        })
        assert status == 201, route
        routes.append(route)
    first, second = routes

    def path(route):
        return f'/api/v1/proxies/{route["id"]}/connections'

    def wait_count(route, count):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            status, connections, headers = client.request(path(route))
            assert status == 200 and 'no-store' in headers['Cache-Control']
            status, proxies, headers = client.request('/api/v1/proxies?search=' + route['destinationHost'])
            assert status == 200 and 'no-store' in headers['Cache-Control']
            listed = next(proxy for proxy in proxies if proxy['id'] == route['id'])
            assert isinstance(listed['liveConnectionCount'], int) and listed['liveConnectionCount'] >= 0
            if len(connections) == count and listed['liveConnectionCount'] == count:
                return connections
            time.sleep(0.05)
        raise AssertionError(f'Expected {count} live connections: {connections}')

    assert client.request('/api/v1/proxies/2147483647/connections')[0] == 404
    assert client.request(path(first), 'POST', {})[0] == 405
    assert wait_count(first, 0) == []
    with socket.create_connection(('127.0.0.1', listener_port), timeout=3) as pending, \
            connect_route(listener_port, first['domain'], backends[0], state=1):
        pending.sendall(handshake(first['domain']))
        assert wait_count(first, 0) == [], 'Pending login and status pings must not count as live players.'

    with connect_route(listener_port, first['domain'], backends[0], player_name='SamePlayer') as earlier, \
            connect_route(listener_port, first['domain'], backends[0], player_name='SamePlayer') as later, \
            connect_route(listener_port, second['domain'], backends[1], player_name='OtherPlayer') as other:
        connections = wait_count(first, 2)
        assert connections[0]['id'] != connections[1]['id'] and connections[0]['id'] > connections[1]['id']
        assert all(entry['playerName'] == 'SamePlayer' and entry['domain'] == first['domain']
                   and entry['connectedAt'].endswith(('+00:00', 'Z')) for entry in connections)
        newest = connections[0]
        history = client.request(f'/api/v1/proxies/{first["id"]}/events')[1]
        earlier.close()
        assert wait_count(first, 1) == [newest], 'Closing an older duplicate name must not remove its newer session.'
        assert client.request(f'/api/v1/proxies/{first["id"]}/events')[1] == history
        other_entry = wait_count(second, 1)
        backends[0].disconnect_clients()
        assert_closed(later)
        assert wait_count(first, 0) == []
        assert wait_count(second, 1) == other_entry
        exchange(other, b'backend disconnect removes only its own live sessions')
    wait_count(second, 0)
    print('PASS: proxy counts and live lists track only established named sessions; duplicate names count separately; client/backend disconnects update only their own proxy without changing history.')

    with ExitStack() as sessions:
        for index in range(25):
            sessions.enter_context(connect_route(listener_port, first['domain'], backends[0], player_name=f'Live{index:02}'))
        assert len(wait_count(first, 25)) == 25, 'The saved-history limit must not cap the live list.'
        with closing(database()) as connection:
            assert connection.execute('SELECT COUNT(*) FROM connection_events WHERE ProxyId=?', (first['id'],)).fetchone()[0] == 20
    wait_count(first, 0)

    backends[0].stop()
    try:
        with socket.create_connection(('127.0.0.1', listener_port), timeout=5) as failed:
            failed.sendall(handshake(first['domain']) + login_start('OfflineBackend'))
            assert_closed(failed)
        assert wait_count(first, 0) == []
    finally:
        backends[0].start()

    with connect_route(listener_port, first['domain'], backends[0], player_name='BeforeEdit') as before_edit, \
            connect_route(listener_port, second['domain'], backends[1], player_name='KeepAlive') as kept:
        wait_count(first, 1)
        other_entry = wait_count(second, 1)
        renamed = {**first, 'domain': 'renamed-live.example.test'}
        assert client.request(f'/api/v1/proxies/{first["id"]}', 'PUT', renamed)[0] == 200
        assert_closed(before_edit)
        assert wait_count(first, 0) == []
        assert wait_count(second, 1) == other_entry
        exchange(kept, b'other live route survives an edit')
        with connect_route(listener_port, renamed['domain'], backends[0], player_name='AfterEdit') as after_edit:
            assert wait_count(first, 1)[0]['domain'] == renamed['domain']
            assert client.request(f'/api/v1/proxies/{first["id"]}', 'DELETE')[0] == 204
            assert_closed(after_edit)
            assert client.request(path(first))[0] == 404
        assert wait_count(second, 1) == other_entry
        exchange(kept, b'other live route survives deletion')
        history = client.request(f'/api/v1/proxies/{second["id"]}/events')[1]
        stop()
        assert_closed(kept)
        start()
        assert wait_count(second, 0) == []
        assert client.request(f'/api/v1/proxies/{second["id"]}/events')[1] == history
    with closing(database()) as connection:
        assert connection.execute("SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name").fetchall() == schemas
    assert client.request(f'/api/v1/proxies/{second["id"]}', 'DELETE')[0] == 204
    print('PASS: live lists can exceed 20, exclude failed backends, reset only edited/deleted routes, and clear on restart while saved history and the database schema stay intact.')


class Client:
    def __init__(self, base_url):
        self.base_url = base_url
        self.csrf = ""
        self.api_key = None
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), NoRedirect()
        )

    def request(self, path, method="GET", body=None, csrf=True, extra_headers=None):
        headers = {"Accept": "application/json"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        if csrf and self.csrf:
            headers["X-CSRF-TOKEN"] = self.csrf
        if self.api_key is not None:
            headers["X-API-Key"] = self.api_key
        headers.update(extra_headers or {})
        request = urllib.request.Request(
            self.base_url + path, method=method, headers=headers,
            data=json.dumps(body).encode() if body is not None else (b"" if method != "GET" else None),
        )
        try:
            response = self.opener.open(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read().decode()
            result = json.loads(raw) if raw and "json" in response.headers.get("Content-Type", "") else raw
            return response.status, result, response.headers

    def session(self):
        status, session, _ = self.request("/api/v1/authentication/session")
        assert status == 200, (status, session)
        self.csrf = session["csrfToken"]
        return session


def verify():
    application = ROOT / "bin/Debug/net10.0/ProxyCraft.dll"
    assert application.exists(), "Run dotnet build first."
    with tempfile.TemporaryDirectory(prefix="proxycraft-tests-") as directory, ExitStack() as resources:
        storage = Path(directory)
        port = free_port()
        listener_port = free_port()
        changed_listener_port = free_port()
        backends = [EchoBackend(label).start() for label in ("backend-a", "backend-b", "backend-c")]
        for backend in backends:
            resources.callback(backend.stop)
        base_url = f"http://127.0.0.1:{port}"
        environment = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "Storage__Directory": str(storage),
                       "Proxy__ListenPort": str(listener_port)}
        server = None
        log = (storage / "server.log").open("a+")

        def start():
            nonlocal server
            server = subprocess.Popen(["dotnet", str(application), "--urls", base_url], cwd=ROOT,
                                      env=environment, stdout=log, stderr=subprocess.STDOUT)
            for attempt in range(150):
                if server.poll() is not None:
                    raise AssertionError((storage / "server.log").read_text())
                try:
                    urllib.request.urlopen(base_url + "/logo.svg", timeout=1).close()
                    return
                except (urllib.error.URLError, TimeoutError):
                    time.sleep(0.1)
            raise AssertionError("The test app did not start.")

        def stop():
            nonlocal server
            if server is not None:
                server.terminate()
                try:
                    server.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()
                server = None

        def database():
            path = storage / "proxycraft.db"
            assert path.exists(), "The app did not create its database on launch."
            return sqlite3.connect(f"file:{path}?mode=ro", uri=True)

        try:
            start()
            client = Client(base_url)
            assert client.session()["authenticationEnabled"] is False
            for page in ("/", "/proxies", "/settings/proxy", "/settings/app"):
                assert client.request(page)[0] == 200, page
            assert client.request("/account/login")[0] == 302
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                status, metrics, headers = client.request("/api/v1/system/metrics")
                assert status == 200 and "no-store" in headers["Cache-Control"]
                required = metrics.values() if Path("/proc/meminfo").exists() else (metrics["totalDiskBytes"], metrics["diskUsagePercent"])
                if all(value is not None for value in required):
                    break
                time.sleep(0.1)
            assert set(metrics) == {"totalCpuCores", "cpuUsagePercent", "totalMemoryBytes", "memoryUsagePercent", "totalDiskBytes", "diskUsagePercent"}
            assert 0 < metrics["totalCpuCores"] <= os.cpu_count()
            for name in ("cpuUsagePercent", "memoryUsagePercent", "diskUsagePercent"):
                assert metrics[name] is None or 0 <= metrics[name] <= 100, (name, metrics)
            assert metrics["totalDiskBytes"] > 0 and metrics["diskUsagePercent"] is not None
            if hasattr(os, "statvfs"):
                filesystem = os.statvfs(storage)
                assert metrics["totalDiskBytes"] == filesystem.f_blocks * filesystem.f_frsize
                assert abs(metrics["diskUsagePercent"] - 100 * (filesystem.f_blocks - filesystem.f_bfree) / filesystem.f_blocks) < 1
            if Path("/proc/meminfo").exists():
                physical_memory = int(re.search(r"^MemTotal:\s+(\d+)", Path("/proc/meminfo").read_text(), re.M)[1]) * 1024
                assert 0 < metrics["totalMemoryBytes"] <= physical_memory
                assert metrics["cpuUsagePercent"] is not None and metrics["memoryUsagePercent"] is not None
            print("PASS: system metrics sample real CPU/RAM/disk, respect capacity bounds, and measure the app-data filesystem.")
            assert client.request("/missing-page")[0] == 404
            with closing(database()) as connection:
                tables = [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
                assert tables == ["api_keys", "connection_events", "proxies", "users"], tables
                for table in tables:
                    columns = {row[1] for row in connection.execute(f"PRAGMA table_info({table})")}
                    assert {"created_at", "modified_at", "is_active"} <= columns
            print("PASS: startup creates users, proxies, API keys, and connection events with BaseModel fields; default access has no login.")

            proxy = {"domain": "PLAY.Example.test", "destinationHost": "192.168.1.10", "destinationPort": 25565}
            assert client.request("/api/v1/proxies", "POST", proxy, csrf=False)[0] == 400
            assert client.request("/api/v1/proxies", "POST", {**proxy, "destinationPort": 0})[0] == 400
            assert client.request("/api/v1/proxies", "POST", {**proxy, "domain": "https://example.test"})[0] == 400
            for domain in ("localhost", "servername", "servername."):
                assert client.request("/api/v1/proxies", "POST", {**proxy, "domain": domain})[0] == 400
            assert client.request("/api/v1/proxies", "POST", {**proxy, "destinationHost": "host.test:25565"})[0] == 400
            status, created, _ = client.request("/api/v1/proxies", "POST", proxy)
            assert status == 201 and created["domain"] == "play.example.test", (status, created)
            assert client.request("/api/v1/proxies", "POST", proxy)[0] == 409
            ipv6 = {"domain": "ipv6.example.test", "destinationHost": "[::1]"}
            status, created_ipv6, _ = client.request("/api/v1/proxies", "POST", ipv6)
            assert status == 201 and created_ipv6["destinationHost"] == "::1" and created_ipv6["destinationPort"] == 25565
            assert len(client.request("/api/v1/proxies?search=PLAY")[1]) == 1
            assert len(client.request("/api/v1/proxies?search=missing")[1]) == 0
            settings = client.request("/api/v1/settings/proxy")[1]
            assert settings["listenPort"] == listener_port and settings["listeningPort"] == listener_port and not settings["listenerError"]
            verify_forwarding(client, created, created_ipv6, backends, listener_port, database)
            # Binding a new shared port preserves existing streams. A failed bind reports the retained port.
            with connect_route(listener_port, "new.example.test", backends[2]) as existing:
                status, saved, _ = client.request("/api/v1/settings/proxy", "PUT", {"listenPort": backends[0].port})
                assert status == 200 and saved["listenerError"] and saved["listeningPort"] == listener_port
                exchange(existing, b"existing stream survives failed listener change")
                status, saved, _ = client.request("/api/v1/settings/proxy", "PUT", {"listenPort": changed_listener_port})
                assert status == 200 and saved["listeningPort"] == changed_listener_port and not saved["listenerError"]
                exchange(existing, b"existing stream survives listener replacement")
                with connect_route(changed_listener_port, "new.example.test", backends[2]) as fresh:
                    exchange(fresh, b"new connections use replacement listener")
            assert client.request("/api/v1/settings/proxy", "PUT", {"listenPort": 65536})[0] == 400
            print("PASS: shared API creates/searches proxies, enforces domain uniqueness, validates ports, and rejects missing CSRF tokens.")

            credentials = {"usernameOrEmail": "Admin", "password": "test-only-password-123"}
            assert client.request("/api/v1/authentication/enable", "POST", credentials)[0] == 200
            anonymous = Client(base_url)
            assert anonymous.session()["authenticationEnabled"] is True
            status, _, headers = anonymous.request("/")
            assert status == 302 and "/account/login" in headers["Location"]
            status, problem, _ = anonymous.request("/api/v1/proxies")
            assert status == 401 and problem["status"] == 401
            assert anonymous.request(f'/api/v1/proxies/{created["id"]}', "PUT", proxy)[0] == 401
            assert anonymous.request(f'/api/v1/proxies/{created["id"]}', "DELETE")[0] == 401
            assert anonymous.request("/api/v1/system/metrics")[0] == 401
            assert anonymous.request(f'/api/v1/proxies/{created["id"]}/events')[0] == 401
            assert anonymous.request(f'/api/v1/proxies/{created["id"]}/connections')[0] == 401
            assert anonymous.request("/api/v1/authentication/disable", "POST")[0] == 401
            assert client.request("/api/v1/authentication/login", "POST", {**credentials, "password": "incorrect-password"})[0] == 401
            assert client.request("/api/v1/authentication/login", "POST", {**credentials, "usernameOrEmail": "ADMIN"})[0] == 204
            assert client.session()["isAuthenticated"] is True
            assert client.request("/api/v1/proxies")[0] == 200
            assert client.request("/api/v1/authentication/enable", "POST", credentials)[0] == 409
            with closing(database()) as connection:
                password_hash, created_at = connection.execute('SELECT PasswordHash, created_at FROM users').fetchone()
                assert password_hash != credentials["password"] and len(password_hash) > 40 and created_at
            print("PASS: enabling auth creates a hashed account, requires a fresh login, and protects both MVC and API access.")

            stop()
            start()
            assert client.session()["isAuthenticated"] is True
            assert client.request("/api/v1/settings/proxy")[1]["listenPort"] == changed_listener_port
            assert len(client.request("/api/v1/proxies")[1]) == 2
            with connect_route(changed_listener_port, "new.example.test", backends[2]) as restored:
                exchange(restored, b"saved routes work after restart")
            assert client.request("/api/v1/authentication/disable", "POST")[0] == 200
            assert client.session()["authenticationEnabled"] is False
            email_credentials = {**credentials, "usernameOrEmail": "owner@example.test"}
            assert client.request("/api/v1/authentication/enable", "POST", email_credentials)[0] == 200
            client.session()
            assert client.request("/api/v1/authentication/login", "POST", email_credentials)[0] == 204
            client.session()
            with closing(database()) as connection:
                assert connection.execute("SELECT COUNT(*) FROM users").fetchone()[0] == 1
                assert connection.execute("SELECT modified_at FROM users").fetchone()[0] is not None
            assert client.request("/api/v1/authentication/logout", "POST")[0] == 204
            assert client.request("/api/v1/proxies")[0] == 401
            print("PASS: persistence survives restart; sign-in accepts a username OR email, updates timestamps, and supports disabling auth and sign-out.")

            for cycle in range(2):
                stop()
                for path in storage.glob("proxycraft.db*"):
                    path.unlink()
                start()
                fresh = Client(base_url)
                assert fresh.session()["authenticationEnabled"] is False
                assert fresh.request("/")[0] == 200
                assert fresh.request("/api/v1/proxies")[1] == []
                with closing(database()) as connection:
                    assert connection.execute("SELECT COUNT(*) FROM users").fetchone()[0] == 0
            print("PASS: deleting the test database while stopped recreates a fresh schema on each launch and resets auth setup.")
            verify_destination_uniqueness(fresh)
            verify_api_keys(fresh, database, stop, start, storage)
            verify_deletion(fresh, backends, changed_listener_port, database, stop, start)
            verify_connection_events(fresh, backends, changed_listener_port, database, stop, start, storage)
            verify_live_connections(fresh, backends, changed_listener_port, database, stop, start)
        finally:
            stop()
            log.close()


if __name__ == "__main__":
    verify()
