"""Check proxycraft:local using disposable containers, a volume, and a TCP backend."""
import json
import socketserver
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

from smoke_test import Client, connect_route, exchange, wait_health

name = 'proxycraft-check-' + uuid.uuid4().hex[:10]
volume = name + '-data'
label = b'docker-backend'

class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            self.request.sendall(label)
            while data := self.request.recv(8192):
                self.request.sendall(data)
        except OSError:
            pass

class BackendServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

class Backend:
    pass

backend = Backend()
backend.label = label
server = BackendServer(('0.0.0.0', 0), Handler)
backend.port = server.server_address[1]
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()

def docker(*arguments):
    result = subprocess.run(['docker', *arguments], capture_output=True, text=True)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()

def start():
    docker('run', '--detach', '--name', name, '--cpus', '1.5', '--memory', '512m',
           '--publish', '127.0.0.1::8080', '--publish', '127.0.0.1::25565',
           '--add-host', 'host.docker.internal:host-gateway',
           '--mount', f'type=volume,source={volume},target=/var/lib/proxycraft', 'proxycraft:local')
    web_port = int(docker('port', name, '8080/tcp').rsplit(':', 1)[1])
    game_port = int(docker('port', name, '25565/tcp').rsplit(':', 1)[1])
    base = f'http://127.0.0.1:{web_port}'
    for attempt in range(200):
        try:
            with urllib.request.urlopen(base + '/logo.svg', timeout=1) as response:
                assert response.status == 200
                return base, game_port
        except OSError:
            time.sleep(0.1)
    raise AssertionError('Container did not become ready: ' + docker('logs', name))

try:
    docker('volume', 'create', volume)
    base, game_port = start()
    assert docker('exec', name, 'id', '-u') == '1654'
    assert docker('exec', name, 'dotnet', '--list-sdks') == ''
    client = Client(base)
    assert client.session()['authenticationEnabled'] is False
    for path in ('/', '/proxies', '/settings/app', '/settings/api'):
        assert client.request(path)[0] == 200
    for attempt in range(80):
        metrics = client.request('/api/v1/system/metrics')[1]
        if metrics['cpuUsagePercent'] is not None:
            break
        time.sleep(0.1)
    # Compare against the actual visible quota: this daemon may apply CPU limits in a hidden ancestor.
    budget, period = docker('exec', name, 'cat', '/sys/fs/cgroup/cpu.max').split()
    if budget != 'max':
        assert metrics['totalCpuCores'] == int(budget) / int(period), metrics
    else:
        assert metrics['totalCpuCores'] > 0, metrics
    assert metrics['totalMemoryBytes'] == 512 * 1024 * 1024, metrics
    assert 0 <= metrics['cpuUsagePercent'] <= 100
    status, route, _ = client.request('/api/v1/proxies', 'POST', {
        'domain': 'docker.example.test', 'destinationHost': 'host.docker.internal', 'destinationPort': backend.port,
    })
    assert status == 201, (status, route)
    wait_health(client, route['id'], True)
    with connect_route(game_port, 'docker.example.test', backend) as connection:
        exchange(connection, b'real Minecraft forwarding through published Docker TCP port')
    credentials = {'usernameOrEmail': 'DockerOwner', 'password': 'docker-test-only-password-123'}
    assert client.request('/api/v1/authentication/enable', 'POST', credentials)[0] == 200
    client.session()
    assert client.request('/api/v1/authentication/login', 'POST', credentials)[0] == 204
    assert client.session()['isAuthenticated']
    status, generated, _ = client.request('/api/v1/settings/api-key', 'POST')
    assert status == 200
    secret = generated['apiKey']
    assert docker('exec', name, 'sh', '-c', 'test -n "$(ls /var/lib/proxycraft/keys/key-*.xml)"') == ''
    docker('stop', '--time', '10', name)
    docker('rm', name)
    base, game_port = start()
    client.base_url = base
    assert client.session()['isAuthenticated'], 'The cookie and data-protection keys must survive container recreation.'
    saved = client.request('/api/v1/proxies')[1]
    assert len(saved) == 1 and saved[0]['id'] == route['id'] and saved[0]['domain'] == route['domain']
    external = Client(base)
    assert external.request('/api/v1/proxies')[0] == 401
    external.api_key = secret
    assert external.request('/api/v1/proxies')[0] == 200
    with connect_route(game_port, 'docker.example.test', backend) as restored:
        exchange(restored, b'route works after container recreation')
    print('PASS: non-root Production container serves HTTP, creates SQLite automatically, exposes the reference and real TCP forwarding, and reports visible CPU counters and the 512-MiB memory limit.', flush=True)
    print('PASS: recreating the container preserves routes, settings, account, API key, cookie keys, and the existing signed-in session.', flush=True)
except Exception:
    logs = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
    print(logs.stdout + logs.stderr, file=sys.stderr)
    raise
finally:
    subprocess.run(['docker', 'rm', '-f', name], capture_output=True)
    subprocess.run(['docker', 'volume', 'rm', volume], capture_output=True)
    server.shutdown()
    server.server_close()
    thread.join(timeout=2)
