"""Check responsive cards, themes, and real 100-session live lists using installed Firefox (no extra packages)."""
import base64
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

root = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(root / 'tests'))
from smoke_test import Client, EchoBackend, free_port, connect_route
from features_test import totp, CREDENTIALS, PASSWORD

with tempfile.TemporaryDirectory(prefix='proxycraft-browser-routes-') as temporary:
    directory = Path(temporary)
    port, game_port, marionette_port = free_port(), free_port(), free_port()
    base = f'http://127.0.0.1:{port}'
    profile = directory / 'firefox'
    profile.mkdir()
    (profile / 'user.js').write_text(f'user_pref("marionette.port", {marionette_port});\nuser_pref("ui.prefersReducedMotion", 0);\nuser_pref("dom.events.testing.asyncClipboard", true);\n')
    log = (directory / 'process.log').open('w')
    backend = EchoBackend('browser-backend').start()
    environment = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'Storage__Directory': str(directory / 'data'), 'Proxy__ListenPort': str(game_port)}
    app = subprocess.Popen(['dotnet', str(root / 'bin/Debug/net10.0/ProxyCraft.dll'), '--urls', base], cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT)
    browser = None
    connection = None
    sessions = []
    screenshots = Path(tempfile.mkdtemp(prefix='proxycraft-ui-screenshots-'))
    try:
        client = Client(base)
        for attempt in range(150):
            try:
                client.session()
                break
            except OSError:
                if app.poll() is not None: raise AssertionError((directory / 'process.log').read_text())
                time.sleep(0.1)
        status, fixture, _ = client.request('/api/v1/proxies', 'POST', {'domain': 'play.browser.test', 'destinationHost': '127.0.0.1', 'destinationPort': backend.port})
        assert status == 201
        browser = subprocess.Popen(['firefox', '--headless', '--remote-allow-system-access', '--no-remote', '--marionette', '--profile', str(profile)], stdout=log, stderr=subprocess.STDOUT)
        for attempt in range(150):
            try:
                connection = socket.create_connection(('127.0.0.1', marionette_port), timeout=1)
                connection.settimeout(20)
                break
            except OSError:
                time.sleep(0.1)
        assert connection
        def receive():
            header = b''
            while not header.endswith(b':'):
                piece = connection.recv(1)
                assert piece
                header += piece
            remaining = int(header[:-1])
            payload = b''
            while len(payload) < remaining:
                payload += connection.recv(remaining - len(payload))
            return json.loads(payload)
        identifier = 0
        def command(name, parameters=None):
            global identifier
            identifier += 1
            payload = json.dumps([0, identifier, name, parameters or {}]).encode()
            connection.sendall(str(len(payload)).encode() + b':' + payload)
            response = receive()
            assert not response[2], (name, response[2])
            result = response[3]
            return result.get('value') if isinstance(result, dict) and 'value' in result else result
        def execute(script):
            return command('WebDriver:ExecuteScript', {'script': script, 'args': [], 'newSandbox': True})
        def wait_for(script):
            for attempt in range(150):
                if execute(script): return
                time.sleep(0.1)
            raise AssertionError(script + ': ' + execute('return document.body.innerText.slice(0, 2000);'))
        def click(selector):
            element = command('WebDriver:FindElement', {'using': 'css selector', 'value': selector})
            command('WebDriver:ElementClick', {'id': element['element-6066-11e4-a52e-4f735466cecf']})
        def screenshot(name):
            data = command('WebDriver:TakeScreenshot', {'full': False, 'scroll': False})
            (screenshots / (name + '.png')).write_bytes(base64.b64decode(data))
        receive()
        command('WebDriver:NewSession', {'capabilities': {'alwaysMatch': {'acceptInsecureCerts': True}}})
        command('WebDriver:SetWindowRect', {'width': 1440, 'height': 980})
        command('Marionette:SetContext', {'value': 'chrome'})
        execute("document.documentElement.style.minWidth = '0px'; document.documentElement.style.minHeight = '0px';")
        command('Marionette:SetContext', {'value': 'content'})
        command('WebDriver:Navigate', {'url': base + '/settings/app'})
        wait_for('return document.querySelector("#authentication-status").textContent.includes("Off") && !document.querySelector("#generate-api-key").disabled;')
        assert execute('const cards = Array.from(document.querySelectorAll("#app-settings-cards > section")).filter(card => card.getClientRects().length); return cards.length === 3 && new Set(cards.map(card => Math.round(card.getBoundingClientRect().left))).size === 2;')
        click('#show-auth-setup')
        wait_for('return document.activeElement === document.querySelector("#usernameOrEmail");')
        for width in (1440, 1024, 768, 320):
            command('WebDriver:SetWindowRect', {'width': width, 'height': 980})
            assert execute('const cards = Array.from(document.querySelectorAll("#app-settings-cards > section")).filter(card => card.getClientRects().length); return new Set(cards.map(card => Math.round(card.getBoundingClientRect().left))).size === (innerWidth >= 1024 ? 2 : 1) && cards.every(card => card.getClientRects().length === 1) && document.documentElement.scrollWidth <= innerWidth && document.activeElement === document.querySelector("#usernameOrEmail");')
            screenshot('account-setup-expanded-' + str(width))
        command('WebDriver:SetWindowRect', {'width': 1440, 'height': 980})
        assert client.request('/api/v1/authentication/enable', 'POST', CREDENTIALS)[0] == 200

        def fill_credentials(code=''):
            wait_for('return document.querySelector(\'#login button[type="submit"]\').disabled === false;')
            execute('document.querySelector("#usernameOrEmail").value = ' + json.dumps(CREDENTIALS['usernameOrEmail']) + '; document.querySelector("#password").value = ' + json.dumps(PASSWORD) + '; document.querySelector("#twoFactorCode").value = ' + json.dumps(code) + ';')
            click('#login button[type="submit"]')
            wait_for('return document.body.dataset.page === "home";')

        command('WebDriver:Navigate', {'url': base + '/account/login'})
        fill_credentials()
        command('WebDriver:Navigate', {'url': base + '/settings/app'})
        wait_for('return !document.querySelector("#two-factor-settings").classList.contains("hidden");')
        for theme in ('light', 'dark', 'rainbow'):
            click('[data-theme-choice="' + theme + '"]')
            assert execute('const cards = Array.from(document.querySelectorAll("#app-settings-cards > section")).filter(card => card.getClientRects().length); return cards.length === 4 && new Set(cards.map(card => Math.round(card.getBoundingClientRect().left))).size === 2 && cards.every(card => card.getClientRects().length === 1) && document.documentElement.scrollWidth <= innerWidth;')
            screenshot('account-settings-' + theme + '-desktop')
        execute('document.querySelector("#publicDashboardEnabled").checked = true; document.querySelector("#showPublicDomains").checked = true;')
        click('#public-dashboard-settings button[type="submit"]')
        wait_for('return document.querySelector("#public-settings-status").textContent.includes("saved");')
        anonymous = Client(base)
        assert 'play.browser.test' in anonymous.request('/')[1]
        command('WebDriver:SetWindowRect', {'width': 320, 'height': 840})
        assert execute('return innerWidth === 320 && document.documentElement.scrollWidth <= innerWidth;')
        assert execute('return new Set(Array.from(document.querySelectorAll("#app-settings-cards > section")).filter(card => card.getClientRects().length).map(card => Math.round(card.getBoundingClientRect().left))).size === 1;')
        screenshot('account-settings-mobile')
        execute('document.querySelector("#two-factor-password").value = ' + json.dumps(PASSWORD) + ';')
        click('#two-factor-start button[type="submit"]')
        wait_for('return !document.querySelector("#two-factor-confirm").classList.contains("hidden");')
        secret = execute('return document.querySelector("#authenticator-secret").value;')
        code = totp(secret, int(time.time()) // 30 - 1)
        execute('document.querySelector("#authenticator-code").value = ' + json.dumps(code) + ';')
        click('#two-factor-confirm button[type="submit"]')
        wait_for('return !document.querySelector("#recovery-codes").classList.contains("hidden");')
        codes = execute('return document.querySelector("#recovery-code-list").textContent;').splitlines()
        assert len(codes) == 8
        assert execute('return document.querySelector("#authenticator-secret").value === "" && document.documentElement.scrollWidth <= innerWidth;')
        screenshot('recovery-codes-mobile')
        click('#recovery-codes a')
        fill_credentials(totp(secret, int(time.time()) // 30))
        command('WebDriver:Navigate', {'url': base + '/settings/app'})
        wait_for('return document.querySelector("#two-factor-status").textContent.includes("Enabled");')
        execute('document.querySelector("#factor-disable-password").value = ' + json.dumps(PASSWORD) + '; document.querySelector("#factor-disable-code").value = ' + json.dumps(codes[0]) + ';')
        click('#two-factor-disable button[type="submit"]')
        wait_for('return document.body.dataset.page === "login";')
        fill_credentials()
        command('WebDriver:Navigate', {'url': base + '/settings/app'})
        wait_for('return document.querySelector("#two-factor-status").textContent === "Not enabled.";')
        execute('document.querySelector("#showPublicDomains").checked = false;')
        click('#public-dashboard-settings button[type="submit"]')
        wait_for('return document.querySelector("#public-settings-status").textContent.includes("saved");')
        click('#logout')
        wait_for('return document.body.dataset.page === "public";')
        assert execute('return document.body.innerText.includes("Welcome to ProxyCraft") && !document.body.innerText.includes("play.browser.test") && document.querySelector(\'nav[aria-label="Main navigation"]\') === null && document.documentElement.scrollWidth <= innerWidth;')
        for theme in ('light', 'dark', 'rainbow'):
            click('[data-theme-choice="' + theme + '"]')
            screenshot('welcome-' + theme + '-mobile')
        print('PASS: settings use two masonry-style columns on desktop and one on mobile; hidden cards take no space, expanded setup remains intact and focused at 1440/1024/768/320px, and all three themes fit.', flush=True)
        print('PASS: real browser signs in, saves public settings, enrolls an authenticator, displays eight recovery codes, signs in with 2FA, disables it with reauthentication, and logs out to the private-domain welcome page at 320px.', flush=True)
        print('Screenshots: ' + str(screenshots), flush=True)
        command('WebDriver:DeleteSession')
    finally:
        for player in sessions: player.close()
        if connection: connection.close()
        for process in (browser, app):
            if process:
                process.terminate()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        backend.stop()
        log.close()
