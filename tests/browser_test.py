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
        command('WebDriver:Navigate', {'url': base + '/proxies'})
        wait_for('return document.querySelectorAll("#proxy-list .proxy-row").length === 1;')
        execute('localStorage.setItem("proxycraft.rainbow-effects", "off"); localStorage.setItem("proxycraft.theme", "rainbow");')
        command('WebDriver:Navigate', {'url': base + '/proxies'})
        wait_for('return document.documentElement.dataset.effects === "on" && document.querySelectorAll("#proxy-list .proxy-row").length === 1;')
        assert execute('return document.querySelector("#rainbow-effects") === null;')
        assert execute('return document.querySelector(\'button[title="Live connections"] svg\') !== null;')
        live_button = 'button[aria-label="Live connections for play.browser.test"]'
        history_button = 'button[aria-label="Connection history for play.browser.test"]'
        status, long_route, _ = client.request('/api/v1/proxies', 'POST', {
            'domain': 'a' * 63 + '.example.test', 'destinationHost': '2001:db8:85a3::8a2e:370:7334', 'destinationPort': 25566,
        })
        assert status == 201, long_route
        execute('document.querySelector("#search").dispatchEvent(new Event("input", {bubbles: true}));')
        wait_for('return document.querySelectorAll("#proxy-list .proxy-row").length === 2;')
        execute('window.originalProxyRow = document.querySelector(\'button[aria-label="Live connections for play.browser.test"]\').closest("tr"); window.originalProxyRow.querySelector(\'button[title="Live connections"]\').focus();')
        assert execute('return Array.from(document.querySelectorAll(".proxy-connection-count")).every(count => count.textContent === "0");')
        for index in range(100):
            sessions.append(connect_route(game_port, fixture['domain'], backend, player_name=f'Browser{index:03}'))
        assert len(client.request(f'/api/v1/proxies/{fixture["id"]}/connections')[1]) == 100
        assert len(client.request(f'/api/v1/proxies/{fixture["id"]}/events')[1]) == 20
        wait_for('return window.originalProxyRow.querySelector(".proxy-connection-count").textContent === "100";')
        assert execute('return window.originalProxyRow.isConnected && document.activeElement === window.originalProxyRow.querySelector(\'button[title="Live connections"]\') && document.querySelector(".proxy-connection-count").textContent === "0";')
        for width, height, size in ((1440, 980, 'desktop'), (768, 900, 'tablet'), (390, 844, 'mobile'), (320, 740, 'narrow')):
            command('WebDriver:SetWindowRect', {'width': width, 'height': height})
            assert execute('return innerWidth;') == width
            for theme in ('light', 'dark', 'rainbow'):
                click('[data-theme-choice="' + theme + '"]')
                assert execute('return document.documentElement.scrollWidth <= innerWidth;')
                assert execute('return window.originalProxyRow.querySelector(".proxy-connection-count").textContent === "100" && window.originalProxyRow.querySelector(".proxy-connection-count").getBoundingClientRect().width > 0;')
                if width < 640:
                    assert execute('return getComputedStyle(document.querySelector(\'a[aria-label="ProxyCraft home"] > span\')).display === "none" && Array.from(document.querySelectorAll(".theme-option > span:not(.rainbow-swatch)")).every(label => getComputedStyle(label).display === "none");')
                    assert execute('return getComputedStyle(document.querySelector(".proxy-row")).display === "grid" && Array.from(document.querySelectorAll(".proxy-actions button")).every(button => button.getBoundingClientRect().width >= 44);')
                else:
                    assert execute('return getComputedStyle(document.querySelector(".proxy-row")).display === "table-row";')
                    assert execute('return Array.from(document.querySelectorAll(".proxy-actions > div")).every(actions => getComputedStyle(actions).flexWrap === "nowrap" && new Set(Array.from(actions.children, button => button.getBoundingClientRect().top)).size === 1);')
                screenshot('cards-' + theme + '-' + size)
                click(live_button)
                wait_for('return document.querySelectorAll("#connection-events li").length === 100;')
                assert execute('const list = document.querySelector("#connection-events"); const dialog = document.querySelector("#events-dialog"); return list.scrollHeight > list.clientHeight && list.clientHeight > 60 && dialog.scrollHeight <= dialog.clientHeight && dialog.getBoundingClientRect().bottom <= innerHeight && dialog.scrollWidth <= dialog.clientWidth;')
                screenshot('live-100-' + theme + '-' + size)
                execute('document.querySelector("#connection-events").scrollTop = 100000;')
                assert execute('return document.querySelector("#close-events").getBoundingClientRect().top >= 0 && document.querySelector("#close-events").getBoundingClientRect().bottom < innerHeight && document.querySelector("#connection-events li:last-child").getBoundingClientRect().bottom <= document.querySelector("#connection-events").getBoundingClientRect().bottom + 1;')
                click('#close-events')
                click(history_button)
                wait_for('return document.querySelectorAll("#connection-events li").length === 20;')
                assert execute('return document.querySelector("#events-dialog-title").textContent === "Connection attempts" && document.querySelector("#events-dialog").scrollHeight <= document.querySelector("#events-dialog").clientHeight;')
                click('#close-events')
        print('PASS: desktop table and mobile cards fit 1440/768/390/320px in Light/Dark/Rainbow; 100 live entries and 20 saved attempts scroll inside the dialog while Close stays visible.', flush=True)
        click(live_button)
        wait_for('return document.querySelectorAll("#connection-events li").length === 100;')
        execute('document.querySelector("#connection-events").scrollTop = 900; window.firstEntry = document.querySelector("#connection-events li");')
        before = execute('const list = document.querySelector("#connection-events"); const top = list.getBoundingClientRect().top; const anchor = Array.from(list.children).find(item => item.getBoundingClientRect().bottom > top); return {id: anchor.dataset.entryId, offset: anchor.getBoundingClientRect().top - top};')
        time.sleep(2.2)
        assert execute('return window.firstEntry === document.querySelector("#connection-events li");'), 'Unchanged polling rebuilt the live list'
        newcomer = connect_route(game_port, fixture['domain'], backend, player_name='NewArrival')
        sessions.append(newcomer)
        wait_for('return document.querySelectorAll("#connection-events li").length === 101;')
        wait_for('return window.originalProxyRow.querySelector(".proxy-connection-count").textContent === "101";')
        assert execute('return window.originalProxyRow.isConnected;')
        after = execute('const list = document.querySelector("#connection-events"); const anchor = Array.from(list.children).find(item => item.dataset.entryId === ' + json.dumps(before['id']) + '); return anchor.getBoundingClientRect().top - list.getBoundingClientRect().top;')
        assert abs(after - before['offset']) < 2, (before, after)
        newcomer.close()
        wait_for('return document.querySelectorAll("#connection-events li").length === 100;')
        wait_for('return window.originalProxyRow.querySelector(".proxy-connection-count").textContent === "100";')
        after = execute('const list = document.querySelector("#connection-events"); const anchor = Array.from(list.children).find(item => item.dataset.entryId === ' + json.dumps(before['id']) + '); return anchor.getBoundingClientRect().top - list.getBoundingClientRect().top;')
        assert abs(after - before['offset']) < 2, (before, after)
        execute('document.querySelector("#connection-events").focus();')
        scrolled = execute('return document.querySelector("#connection-events").scrollTop;')
        command('WebDriver:PerformActions', {'actions': [{'type': 'key', 'id': 'keyboard', 'actions': [{'type': 'keyDown', 'value': chr(0xe00f)}, {'type': 'keyUp', 'value': chr(0xe00f)}]}]})
        wait_for('return document.querySelector("#connection-events").scrollTop > ' + str(scrolled) + ';')
        click('#close-events')
        print('PASS: unchanged polls keep existing DOM; joins/disconnects preserve the visible entry and keyboard scrolling works.', flush=True)
        click('[data-theme-choice="rainbow"]')
        command('Marionette:SetContext', {'value': 'chrome'})
        execute('Services.prefs.setIntPref("ui.prefersReducedMotion", 1);')
        command('Marionette:SetContext', {'value': 'content'})
        wait_for('return matchMedia("(prefers-reduced-motion: reduce)").matches && document.documentElement.dataset.effects === "off";')
        command('Marionette:SetContext', {'value': 'chrome'})
        execute('Services.prefs.setIntPref("ui.prefersReducedMotion", 0);')
        command('Marionette:SetContext', {'value': 'content'})
        wait_for('return document.documentElement.dataset.effects === "on";')
        print('PASS: Rainbow always animates despite the retired stored off preference, has no effects control, and respects device reduced-motion changes.', flush=True)
        click('button[aria-label="Copy play.browser.test"]')
        wait_for('return document.querySelector("#proxy-feedback").textContent === "Copied play.browser.test";')
        click('button[aria-label="Edit play.browser.test"]')
        wait_for('return document.querySelector("#proxy-dialog").open && document.querySelector("#domain").value === "play.browser.test";')
        click('#cancel-proxy')
        execute('document.querySelector("#search").value = "does-not-exist"; document.querySelector("#search").dispatchEvent(new Event("input", {bubbles: true}));')
        wait_for('return document.querySelector("#proxy-list").textContent.includes("No matching proxies.");')
        assert execute('return document.documentElement.scrollWidth <= innerWidth;')
        execute('document.querySelector("#search").value = "play.browser.test"; document.querySelector("#search").dispatchEvent(new Event("input", {bubbles: true}));')
        wait_for('return document.querySelectorAll("#proxy-list .proxy-row").length === 1;')
        click('button[title="Delete proxy"]')
        wait_for('return document.querySelector("#proxy-list").textContent.includes("No matching proxies.");')
        print('PASS: mobile copy/edit/search/delete actions still work. Screenshots: ' + str(screenshots), flush=True)
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
