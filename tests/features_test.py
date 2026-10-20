"""Exercise public visibility, the real SignalR WebSocket feed, and optional user 2FA with temporary storage."""
import base64
from concurrent.futures import ThreadPoolExecutor
import hashlib
import hmac
import html
import json
import os
from pathlib import Path
import re
import socket
import sqlite3
import struct
import subprocess
import tempfile
import time
import urllib.parse

from smoke_test import Client, free_port, receive

ROOT = Path(__file__).resolve().parent.parent
PASSWORD = 'a-long-test-password-123'
CREDENTIALS = {'usernameOrEmail': 'owner@example.test', 'password': PASSWORD}


def totp(secret, step):
    digest = hmac.new(base64.b32decode(secret), struct.pack('>Q', step), hashlib.sha1).digest()
    offset = digest[-1] & 15
    return f'{(struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000:06d}'


class DomainSocket:
    def __init__(self, client, port):
        status, negotiated, _ = client.request('/api/v1/hubs/domains/negotiate?negotiateVersion=1', 'POST')
        assert status == 200, negotiated
        self.connection = socket.create_connection(('127.0.0.1', port), timeout=5)
        token = urllib.parse.quote(negotiated['connectionToken'], safe='')
        key = base64.b64encode(os.urandom(16)).decode()
        headers = (f'GET /api/v1/hubs/domains?id={token} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n'
                   f'Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n'
                   f'Sec-WebSocket-Version: 13\r\nX-API-Key: {client.api_key}\r\n\r\n')
        self.connection.sendall(headers.encode())
        response = b''
        while not response.endswith(b'\r\n\r\n'):
            response += receive(self.connection, 1)
        assert b'101 Switching Protocols' in response, response
        self.send(b'{"protocol":"json","version":1}\x1e')
        self.pending = []
        assert self.message() == {}

    def send(self, payload):
        mask = os.urandom(4)
        assert len(payload) < 126
        self.connection.sendall(bytes([0x81, 0x80 | len(payload)]) + mask + bytes(value ^ mask[index % 4] for index, value in enumerate(payload)))

    def message(self):
        while not self.pending:
            header = receive(self.connection, 2)
            length = header[1] & 127
            if length == 126:
                length = struct.unpack('>H', receive(self.connection, 2))[0]
            elif length == 127:
                length = struct.unpack('>Q', receive(self.connection, 8))[0]
            payload = receive(self.connection, length)
            if header[0] & 15 == 8:
                raise ConnectionAbortedError('Hub closed the connection')
            self.pending.extend(json.loads(part) for part in payload.split(b'\x1e') if part)
        return self.pending.pop(0)

    def event(self):
        while True:
            message = self.message()
            if message.get('type') == 1:
                return message

    def close(self):
        self.connection.close()

    def assert_no_event(self):
        self.connection.settimeout(0.25)
        try:
            raise AssertionError(('Unexpected domain event', self.event()))
        except TimeoutError:
            pass
        finally:
            self.connection.settimeout(5)


def verify():
    with tempfile.TemporaryDirectory(prefix='proxycraft-features-') as temporary:
        directory = Path(temporary)
        data = directory / 'data'
        data.mkdir()
        # A real pre-2FA users table proves startup adds optional columns without replacing the user.
        with sqlite3.connect(data / 'proxycraft.db') as database:
            database.execute('CREATE TABLE users (Id INTEGER PRIMARY KEY, UsernameOrEmail TEXT NOT NULL, NormalizedUsernameOrEmail TEXT NOT NULL, PasswordHash TEXT NOT NULL, SecurityStamp TEXT NOT NULL, created_at TEXT NOT NULL, modified_at TEXT NULL, is_active INTEGER NOT NULL)')
            database.execute("INSERT INTO users VALUES (1, 'legacy-owner', 'LEGACY-OWNER', 'legacy-hash', 'legacy-stamp', '2026-01-01', NULL, 1)")
            database.execute('CREATE TABLE proxies (Id INTEGER PRIMARY KEY AUTOINCREMENT, Domain TEXT NOT NULL UNIQUE, DestinationHost TEXT NOT NULL, DestinationPort INTEGER NOT NULL, created_at TEXT NOT NULL, modified_at TEXT NULL, is_active INTEGER NOT NULL)')
        web_port, game_port = free_port(), free_port()
        legacy_settings = json.dumps({'listenPort': game_port, 'publicDashboardMessage': 'Legacy welcome text'})
        (data / 'settings.json').write_text(legacy_settings)
        base = f'http://127.0.0.1:{web_port}'
        log = (directory / 'app.log').open('a')
        app = None
        sockets = []

        def stop():
            nonlocal app
            if app is not None:
                app.terminate()
                try:
                    app.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    app.kill()
                    app.wait()
                app = None

        def start():
            nonlocal app
            app = subprocess.Popen(['dotnet', str(ROOT / 'bin/Debug/net10.0/ProxyCraft.dll'), '--urls', base], cwd=ROOT,
                                   env={**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'Storage__Directory': str(data),
                                        'DataProtection__KeysDirectory': str(data / 'keys'), 'Proxy__ListenPort': str(game_port)},
                                   stdout=log, stderr=subprocess.STDOUT)
            for attempt in range(200):
                try:
                    if Client(base).request('/account/login')[0] in (200, 302):
                        return
                except OSError:
                    pass
                assert app.poll() is None, (directory / 'app.log').read_text()
                time.sleep(0.1)
            raise AssertionError('App startup timed out')

        def refresh(client):
            status, page, _ = client.request('/')
            if status == 302:
                status, page, _ = client.request('/account/login')
            assert status == 200
            client.csrf = html.unescape(re.search(r'<meta name="csrf-token" content="([^"]+)"', page)[1])
            return client.session()

        def browser():
            client = Client(base)
            refresh(client)
            return client

        try:
            start()
            with sqlite3.connect(data / 'proxycraft.db') as database:
                columns = {row[1] for row in database.execute('PRAGMA table_info(users)')}
                assert {'TwoFactorSecret', 'RecoveryCodeHashes', 'LastTotpStep'} <= columns
                assert database.execute('SELECT UsernameOrEmail,PasswordHash,SecurityStamp,TwoFactorSecret FROM users').fetchone() == ('legacy-owner', 'legacy-hash', 'legacy-stamp', None)
            admin = browser()
            initial = admin.request('/api/v1/settings/app')[1]
            assert initial['publicDashboardMessage'] == 'Legacy welcome text'
            assert initial['publicDashboardTitle'] == 'Welcome to ProxyCraft'
            assert (data / 'settings.json').read_text() == legacy_settings
            assert admin.request('/dashboard')[0] == 200
            assert admin.request('/api/v1/public/dashboard')[0] == 404
            status, route, _ = admin.request('/api/v1/proxies', 'POST', {'domain': 'public.example.test', 'destinationHost': '127.0.0.1', 'destinationPort': free_port()})
            assert status == 201
            assert admin.request('/api/v1/authentication/enable', 'POST', CREDENTIALS)[0] == 200
            assert admin.request('/api/v1/authentication/login', 'POST', CREDENTIALS)[0] == 204
            refresh(admin)
            anonymous = browser()
            assert anonymous.request('/')[0] == 302
            assert anonymous.request('/dashboard')[0] == 302
            assert admin.request('/api/v1/settings/app', 'PUT', {'publicDashboardEnabled': True, 'showPublicDomains': False})[0] == 200
            status, welcome, _ = anonymous.request('/')
            assert status == 200 and 'Welcome to ProxyCraft' in welcome and 'Proxy overview' not in welcome
            assert 'public.example.test' not in welcome and 'System resources' not in welcome
            assert anonymous.request('/api/v1/public/dashboard')[1] == {'domains': [], 'message': 'Legacy welcome text', 'title': 'Welcome to ProxyCraft'}
            for path in ('/api/v1/proxies', '/api/v1/settings/app', '/api/v1/system/metrics', f'/api/v1/proxies/{route["id"]}/events'):
                assert anonymous.request(path)[0] == 401, path
            for message in ('', '   ', 'x' * 501):
                assert admin.request('/api/v1/settings/app', 'PUT', {'publicDashboardEnabled': True, 'showPublicDomains': True, 'publicDashboardMessage': message})[0] == 400
            for title in ('', '   ', 'x' * 121):
                assert admin.request('/api/v1/settings/app', 'PUT', {'publicDashboardEnabled': True, 'showPublicDomains': True, 'publicDashboardTitle': title})[0] == 400
            title = '<b>Builders gate</b>'
            saved = admin.request('/api/v1/settings/app', 'PUT', {'publicDashboardEnabled': True, 'publicDashboardTitle': '  ' + title + '  '})[1]
            assert saved['publicDashboardTitle'] == title and saved['publicDashboardMessage'] == 'Legacy welcome text'
            message = 'Welcome, builders! <script>alert("unsafe")</script>'
            assert admin.request('/api/v1/settings/app', 'PUT', {'publicDashboardEnabled': True, 'showPublicDomains': True, 'publicDashboardMessage': '  ' + message + '  '})[0] == 200
            public = anonymous.request('/api/v1/public/dashboard')[1]
            assert public == {'domains': ['public.example.test'], 'message': message, 'title': title}
            welcome = anonymous.request('/')[1]
            assert 'public.example.test' in welcome and 'data-copy-domain="public.example.test"' in welcome
            assert '&lt;script&gt;' in welcome and '<script>alert("unsafe")</script>' not in welcome
            assert re.search(r'<h1[^>]*>&lt;b&gt;Builders gate&lt;/b&gt;</h1>', welcome)
            assert re.search(r'<p class="page-description[^>]*>Welcome, builders! &lt;script&gt;', welcome)
            preview = admin.request('/dashboard')[1]
            assert 'data-page="dashboard"' in preview and 'aria-label="Main navigation"' in preview
            assert anonymous.request('/dashboard')[0] == 302
            print('PASS: existing user survives automatic optional-column creation; anonymous welcome/domain visibility is opt-in and never grants management, metrics, or player access.', flush=True)

            assert admin.request('/api/v1/hubs/domains/negotiate?negotiateVersion=1', 'POST')[0] == 401
            api_key = admin.request('/api/v1/settings/api-key', 'POST')[1]['apiKey']
            external = Client(base)
            external.api_key = api_key
            first = DomainSocket(external, web_port)
            sockets.append(first)
            assert anonymous.request('/api/v1/public/dashboard', csrf=False)[1] == public
            status, created, _ = admin.request('/api/v1/proxies', 'POST', {'domain': 'hub.example.test', 'destinationHost': '127.0.0.1', 'destinationPort': free_port()})
            assert status == 201
            event = first.event()
            assert event['target'] == 'DomainAdded' and event['arguments'][0]['domain'] == created['domain']
            assert event['arguments'][0]['proxyId'] == created['id'] and 'occurredAt' in event['arguments'][0]
            assert admin.request('/api/v1/proxies', 'POST', {**created, 'destinationPort': free_port()})[0] == 409
            first.assert_no_event()
            assert admin.request(f'/api/v1/proxies/{created["id"]}', 'PUT', {**created, 'domain': 'renamed.hub.test'})[0] == 200
            removed, added = first.event(), first.event()
            assert removed['target'] == 'DomainRemoved' and removed['arguments'][0]['domain'] == 'hub.example.test'
            assert added['target'] == 'DomainAdded' and added['arguments'][0]['domain'] == 'renamed.hub.test'
            assert admin.request(f'/api/v1/proxies/{created["id"]}', 'PUT', {**created, 'domain': 'renamed.hub.test', 'destinationPort': free_port()})[0] == 200
            first.assert_no_event()
            assert admin.request(f'/api/v1/proxies/{created["id"]}', 'DELETE')[0] == 204
            assert first.event()['target'] == 'DomainRemoved'
            external.api_key = admin.request('/api/v1/settings/api-key', 'POST')[1]['apiKey']
            second = DomainSocket(external, web_port)
            sockets.append(second)
            assert admin.request('/api/v1/proxies', 'POST', {'domain': 'rotated.hub.test', 'destinationHost': '127.0.0.1', 'destinationPort': free_port()})[0] == 201
            assert second.event()['arguments'][0]['domain'] == 'rotated.hub.test'
            try:
                leaked = first.event()
                raise AssertionError(('Revoked key received an event', leaked))
            except (ConnectionAbortedError, ConnectionResetError, AssertionError) as error:
                assert not isinstance(error, AssertionError) or 'disconnected unexpectedly' in str(error)
            assert external.request('/api/v1/authentication/two-factor')[0] == 401
            print('PASS: a real key-authenticated SignalR WebSocket receives add/rename/delete immediately; a rotated key cannot receive later events or manage user 2FA.', flush=True)
            for feed in sockets:
                feed.close()
            sockets.clear()

            previous_session = browser()
            assert previous_session.request('/api/v1/authentication/login', 'POST', CREDENTIALS)[0] == 204
            refresh(previous_session)
            assert admin.request('/api/v1/authentication/two-factor/setup', 'POST', {'password': 'incorrect-password'})[0] == 400
            status, setup, _ = admin.request('/api/v1/authentication/two-factor/setup', 'POST', {'password': PASSWORD})
            assert status == 200 and len(setup['secret']) == 32 and setup['authenticatorUri'].startswith('otpauth://totp/')
            assert admin.request('/api/v1/authentication/two-factor')[1]['isEnabled'] is False
            invalid = {'setupToken': 'tampered-' + setup['setupToken'], 'code': '123456'}
            assert admin.request('/api/v1/authentication/two-factor/confirm', 'POST', invalid)[0] == 400
            accepted_codes = {totp(setup['secret'], int(time.time()) // 30 + offset) for offset in (-1, 0, 1)}
            wrong_code = next(f'{candidate:06d}' for candidate in range(10) if f'{candidate:06d}' not in accepted_codes)
            assert admin.request('/api/v1/authentication/two-factor/confirm', 'POST', {'setupToken': setup['setupToken'], 'code': wrong_code})[0] == 400
            previous_code = totp(setup['secret'], int(time.time()) // 30 - 1)
            status, enabled, _ = admin.request('/api/v1/authentication/two-factor/confirm', 'POST', {'setupToken': setup['setupToken'], 'code': previous_code})
            assert status == 200, enabled
            codes = enabled['recoveryCodes']
            assert len(codes) == len(set(codes)) == 8
            assert previous_session.request('/api/v1/proxies')[0] == 401
            with sqlite3.connect(data / 'proxycraft.db') as database:
                encrypted, hashes = database.execute('SELECT TwoFactorSecret,RecoveryCodeHashes FROM users WHERE Id=1').fetchone()
                assert setup['secret'] not in encrypted and all(code not in hashes for code in codes)
            stop()
            start()
            client = browser()
            assert client.request('/api/v1/authentication/login', 'POST', CREDENTIALS)[0] == 401
            assert client.request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': wrong_code})[0] == 401
            current_code = totp(setup['secret'], int(time.time()) // 30)
            assert client.request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': current_code})[0] == 204
            refresh(client)
            replay = browser()
            assert replay.request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': current_code})[0] == 401
            assert replay.request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': codes[0]})[0] == 204
            refresh(replay)
            assert browser().request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': codes[0]})[0] == 401
            contenders = [browser(), browser()]
            with ThreadPoolExecutor(max_workers=2) as workers:
                statuses = list(workers.map(lambda contender: contender.request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': codes[1]})[0], contenders))
            assert sorted(statuses) == [204, 401], statuses
            assert client.request('/api/v1/authentication/two-factor')[1] == {'isEnabled': True, 'recoveryCodesRemaining': 6}
            print('PASS: authenticator enrollment requires password/confirmation; encrypted secrets and hashed recovery codes persist; old sessions and replayed/concurrently reused codes are rejected.', flush=True)
            stop()
            start()
            owner = browser()
            assert owner.request('/api/v1/authentication/login', 'POST', {**CREDENTIALS, 'twoFactorCode': codes[2]})[0] == 204
            refresh(owner)
            assert owner.request('/api/v1/settings/app')[1]['publicDashboardMessage'] == message
            assert owner.request('/api/v1/settings/app')[1]['publicDashboardTitle'] == title
            assert owner.request('/api/v1/authentication/two-factor/disable', 'POST', {'password': PASSWORD, 'code': codes[3]})[0] == 204
            plain = browser()
            assert plain.request('/api/v1/authentication/login', 'POST', CREDENTIALS)[0] == 204
            refresh(plain)
            assert plain.request('/api/v1/authentication/two-factor')[1]['isEnabled'] is False
            status, saved, _ = plain.request('/api/v1/settings/app', 'PUT', {'publicDashboardEnabled': False, 'showPublicDomains': True})
            assert status == 200 and saved['showPublicDomains'] is False and saved['publicDashboardMessage'] == message
            assert saved['publicDashboardTitle'] == title
            with sqlite3.connect(data / 'proxycraft.db') as database:
                assert database.execute('SELECT COUNT(*) FROM users').fetchone()[0] == 1
            assert json.loads((data / 'settings.json').read_text())['showPublicDomains'] is False
            assert 'data-page="dashboard"' in plain.request('/dashboard')[1]
            assert browser().request('/')[0] == 302
            assert Client(base).request('/api/v1/public/dashboard')[0] == 404
            print('PASS: removing 2FA requires password plus a valid code and signs out sessions; disabling the public dashboard hides the welcome page and domain API.', flush=True)
        finally:
            for feed in sockets:
                feed.close()
            stop()
            log.close()


if __name__ == '__main__':
    verify()
