const page = document.body.dataset.page;
const csrf = document.querySelector('meta[name="csrf-token"]');

function showError(error, target = document.querySelector("#page-error")) {
    target.textContent = error.message || "The request could not be completed.";
    target.classList.remove("hidden");
}

async function api(path, options = {}) {
    const headers = { ...options.headers };
    if (options.body !== undefined) {
        headers["Content-Type"] = "application/json";
    }
    headers["X-CSRF-TOKEN"] = csrf.content;
    const response = await fetch(`/api/v1${path}`, { ...options, headers, credentials: "same-origin" });
    if (!response.ok) {
        if (response.status === 401 && path !== "/authentication/login") {
            window.location.assign("/account/login");
        }
        const problem = await response.json().catch(() => ({}));
        const detail = problem.errors ? Object.values(problem.errors).flat().join(" ") : problem.title;
        throw new Error(detail || `The request failed (${response.status}). Please try again.`);
    }
    return response.status === 204 ? null : response.json();
}

function handleForm(id, action, errorTarget) {
    const form = document.querySelector(id);
    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        const button = form.querySelector('button[type="submit"]');
        const error = errorTarget || document.querySelector("#page-error");
        error.classList.add("hidden");
        button.disabled = true;
        try {
            await action(new FormData(form));
        } catch (failure) {
            showError(failure, error);
        } finally {
            button.disabled = false;
        }
    });
    form.querySelector('button[type="submit"]').disabled = false;
}

async function initialize() {
    const session = await api("/authentication/session");
    csrf.content = session.csrfToken;
    const logout = document.querySelector("#logout");
    logout.classList.toggle("hidden", !session.isAuthenticated);
    logout.addEventListener("click", async () => {
        logout.disabled = true;
        try {
            await api("/authentication/logout", { method: "POST" });
            window.location.assign("/");
        } catch (error) {
            showError(error);
            logout.disabled = false;
        }
    });

    if (page === "home") {
        initializeSystemMetrics();
        const [settings, proxies] = await Promise.all([api("/settings/proxy"), api("/proxies")]);
        document.querySelector("#listen-port").textContent = settings.listenPort;
        document.querySelector("#proxy-count").textContent = proxies.length;
    }

    if (page === "proxy-settings") {
        const settings = await api("/settings/proxy");
        document.querySelector("#listenPort").value = settings.listenPort;
        if (settings.listenerError) {
            showError(new Error(settings.listenerError));
        }
        handleForm("#proxy-settings", async (data) => {
            document.querySelector("#settings-saved").classList.add("hidden");
            const saved = await api("/settings/proxy", { method: "PUT", body: JSON.stringify({ listenPort: Number(data.get("listenPort")) }) });
            if (saved.listenerError) {
                throw new Error(`Settings saved. ${saved.listenerError}`);
            }
            document.querySelector("#settings-saved").classList.remove("hidden");
        });
    }

    if (page === "app-settings") {
        const settings = await api("/settings/app");
        document.querySelector("#publicDashboardEnabled").checked = settings.publicDashboardEnabled;
        document.querySelector("#showPublicDomains").checked = settings.showPublicDomains;
        handleForm("#public-dashboard-settings", async (data) => {
            await api("/settings/app", { method: "PUT", body: JSON.stringify({
                publicDashboardEnabled: data.has("publicDashboardEnabled"),
                showPublicDomains: data.has("showPublicDomains")
            }) });
            document.querySelector("#public-settings-status").textContent = "Public dashboard settings saved.";
        });
        if (session.isAuthenticated) {
            await initializeTwoFactor();
        }
        document.querySelector("#authentication-status").textContent = settings.authenticationEnabled ? "On — sign-in required" : "Off — no sign-in required";
        document.querySelector("#auth-disabled").classList.toggle("hidden", settings.authenticationEnabled);
        document.querySelector("#auth-enabled").classList.toggle("hidden", !settings.authenticationEnabled);
        document.querySelector("#show-auth-setup").addEventListener("click", (event) => {
            event.currentTarget.classList.add("hidden");
            document.querySelector("#auth-setup").classList.remove("hidden");
            document.querySelector("#usernameOrEmail").focus();
        });
        handleForm("#auth-setup", async (data) => {
            await api("/authentication/enable", { method: "POST", body: JSON.stringify(Object.fromEntries(data)) });
            window.location.assign("/account/login");
        });
        document.querySelector("#disable-auth").addEventListener("click", async (event) => {
            event.currentTarget.disabled = true;
            try {
                await api("/authentication/disable", { method: "POST" });
                window.location.reload();
            } catch (error) {
                showError(error);
                document.querySelector("#disable-auth").disabled = false;
            }
        });

        const apiSettings = await api("/settings/api-key");
        function updateApiKeyStatus(settings) {
            document.querySelector("#api-key-status").textContent = settings.isConfigured
                ? `Key configured (${settings.keyPrefix}…). External API requests require it.` : "No API key configured.";
            document.querySelector("#generate-api-key").textContent = settings.isConfigured ? "Regenerate API key" : "Generate API key";
        }
        updateApiKeyStatus(apiSettings);
        handleForm("#api-key-generate", async () => {
            const generated = await api("/settings/api-key", { method: "POST" });
            updateApiKeyStatus({ isConfigured: true, keyPrefix: generated.keyPrefix });
            const field = document.querySelector("#generated-api-key");
            field.value = generated.apiKey;
            document.querySelector("#api-key-generated").classList.remove("hidden");
            field.focus();
            field.select();
        });
    }

    if (page === "login") {
        handleForm("#login", async (data) => {
            await api("/authentication/login", { method: "POST", body: JSON.stringify(Object.fromEntries(data)) });
            window.location.assign("/");
        });
    }

    if (page === "proxies") {
        initializeProxies();
    }
}

async function initializeTwoFactor() {
    const status = await api("/authentication/two-factor");
    document.querySelector("#two-factor-settings").classList.remove("hidden");
    document.querySelector("#two-factor-status").textContent = status.isEnabled
        ? `Enabled. ${status.recoveryCodesRemaining} recovery codes remain.` : "Not enabled.";
    document.querySelector("#two-factor-start").classList.toggle("hidden", status.isEnabled);
    document.querySelector("#two-factor-disable").classList.toggle("hidden", !status.isEnabled);

    handleForm("#two-factor-start", async (data) => {
        const setup = await api("/authentication/two-factor/setup", {
            method: "POST", body: JSON.stringify(Object.fromEntries(data))
        });
        document.querySelector("#two-factor-password").value = "";
        document.querySelector("#authenticator-secret").value = setup.secret;
        document.querySelector("#authenticator-link").href = setup.authenticatorUri;
        document.querySelector("#two-factor-token").value = setup.setupToken;
        document.querySelector("#two-factor-start").classList.add("hidden");
        document.querySelector("#two-factor-confirm").classList.remove("hidden");
        document.querySelector("#authenticator-code").focus();
    });
    handleForm("#two-factor-confirm", async (data) => {
        const enabled = await api("/authentication/two-factor/confirm", {
            method: "POST", body: JSON.stringify(Object.fromEntries(data))
        });
        document.querySelector("#recovery-code-list").textContent = enabled.recoveryCodes.join("\n");
        document.querySelector("#recovery-codes").classList.remove("hidden");
        document.querySelector("#two-factor-confirm").reset();
        document.querySelector("#two-factor-confirm").classList.add("hidden");
        document.querySelector("#two-factor-status").textContent = "Enabled. Save the recovery codes, then sign in again.";
    });
    handleForm("#two-factor-disable", async (data) => {
        await api("/authentication/two-factor/disable", {
            method: "POST", body: JSON.stringify(Object.fromEntries(data))
        });
        window.location.assign("/account/login");
    });
}

function initializeSystemMetrics() {
    const number = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
    const status = document.querySelector("#system-metrics-status");
    let loading = false;

    function bytes(value) {
        if (value === null) {
            return "Unavailable";
        }
        const units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        const index = Math.min(Math.floor(Math.log2(Math.max(value, 1)) / 10), units.length - 1);
        return `${number.format(value / 1024 ** index)} ${units[index]} total`;
    }

    function updateUsage(name, value) {
        document.querySelector(`#${name}-usage`).textContent = value === null ? "Usage unavailable" : `${number.format(value)}% in use`;
        const meter = document.querySelector(`#${name}-meter`);
        meter.classList.toggle("hidden", value === null);
        if (value !== null) {
            meter.value = value;
        }
    }

    async function load() {
        if (loading) {
            return;
        }
        loading = true;
        try {
            const metrics = await api("/system/metrics");
            document.querySelector("#cpu-total").textContent = metrics.totalCpuCores === null ? "Capacity unavailable" : `${number.format(metrics.totalCpuCores)} vCPUs total`;
            document.querySelector("#memory-total").textContent = bytes(metrics.totalMemoryBytes);
            document.querySelector("#disk-total").textContent = bytes(metrics.totalDiskBytes);
            updateUsage("cpu", metrics.cpuUsagePercent);
            updateUsage("memory", metrics.memoryUsagePercent);
            updateUsage("disk", metrics.diskUsagePercent);
            status.textContent = "Usage refreshes every 5 seconds.";
        } catch (failure) {
            updateUsage("cpu", null);
            updateUsage("memory", null);
            updateUsage("disk", null);
            document.querySelector("#cpu-total").textContent = "Capacity unavailable";
            document.querySelector("#memory-total").textContent = "Capacity unavailable";
            document.querySelector("#disk-total").textContent = "Capacity unavailable";
            status.textContent = "Unable to load system resources. Retrying automatically.";
        } finally {
            loading = false;
        }
    }
    load();
    setInterval(() => {
        if (!document.hidden) {
            load();
        }
    }, 5000);
}

function initializeProxies() {
    const search = document.querySelector("#search");
    const list = document.querySelector("#proxy-list");
    const dialog = document.querySelector("#proxy-dialog");
    const error = document.querySelector("#proxy-create-error");
    const form = document.querySelector("#proxy-create");
    const domainField = form.elements.domain;
    const domainError = document.querySelector("#domain-error");
    const feedback = document.querySelector("#proxy-feedback");
    const destinationWarning = document.querySelector("#destination-host-warning");
    const eventsDialog = document.querySelector("#events-dialog");
    const eventsList = document.querySelector("#connection-events");
    const eventsStatus = document.querySelector("#events-status");
    const eventsError = document.querySelector("#events-error");
    const statuses = new Map();
    const connectionCounts = new Map();
    let activeRequest;
    let searchTimer;
    let editingProxyId = null;
    let renderedConfiguration;
    let eventsProxyId = null;
    let eventsRequest;
    let renderedEvents;
    let showingLiveConnections = false;

    async function loadEvents() {
        const proxyId = eventsProxyId;
        const live = showingLiveConnections;
        eventsRequest?.abort();
        const request = new AbortController();
        eventsRequest = request;
        try {
            const events = await api(`/proxies/${proxyId}/${live ? "connections" : "events"}`, { signal: request.signal });
            if (eventsRequest !== request || !eventsDialog.open || eventsProxyId !== proxyId || showingLiveConnections !== live) {
                return;
            }
            eventsError.classList.add("hidden");
            const configuration = JSON.stringify(events);
            if (configuration === renderedEvents) {
                return;
            }
            renderedEvents = configuration;
            const scrollTop = eventsList.scrollTop;
            const listTop = eventsList.getBoundingClientRect().top;
            const anchor = scrollTop > 0 ? Array.from(eventsList.children).find((item) => item.getBoundingClientRect().bottom > listTop) : null;
            const anchorOffset = anchor ? anchor.getBoundingClientRect().top - listTop : 0;
            const items = document.createDocumentFragment();
            eventsStatus.textContent = live
                ? events.length ? `${events.length} live ${events.length === 1 ? "connection" : "connections"}. Refreshes every 2 seconds.` : "No live connections."
                : events.length ? `${events.length} of up to 20 saved attempts. Refreshes every 5 seconds.` : "No connection attempts recorded yet.";
            for (const entry of events) {
                const item = document.createElement("li");
                item.dataset.entryId = String(entry.id);
                item.className = "connection-entry py-3";
                const message = document.createElement("p");
                message.className = "[overflow-wrap:anywhere] text-sm text-body";
                message.textContent = live ? `${entry.playerName} → ${entry.domain}` : `${entry.playerName} tried to connect to ${entry.domain}.`;
                const time = document.createElement("time");
                time.className = "mt-1 block text-xs text-muted";
                time.dateTime = live ? entry.connectedAt : entry.createdAt;
                time.textContent = `${live ? "Connected " : ""}${new Date(time.dateTime).toLocaleString()}`;
                item.append(message, time);
                items.append(item);
            }
            eventsList.replaceChildren(items);
            const replacement = anchor ? Array.from(eventsList.children).find((item) => item.dataset.entryId === anchor.dataset.entryId) : null;
            eventsList.scrollTop = scrollTop;
            if (replacement) {
                eventsList.scrollTop += replacement.getBoundingClientRect().top - eventsList.getBoundingClientRect().top - anchorOffset;
            }
        } catch (failure) {
            if (failure.name === "AbortError" || eventsRequest !== request || !eventsDialog.open || eventsProxyId !== proxyId || showingLiveConnections !== live) {
                return;
            }
            eventsList.replaceChildren();
            renderedEvents = undefined;
            eventsStatus.textContent = live ? "Unable to load live connections. Retrying automatically." : "Unable to load connection attempts. Retrying automatically.";
            showError(failure, eventsError);
        } finally {
            if (eventsRequest === request) {
                eventsRequest = undefined;
            }
        }
    }

    function openEvents(proxy, live = false) {
        eventsProxyId = proxy.id;
        showingLiveConnections = live;
        renderedEvents = undefined;
        eventsList.replaceChildren();
        eventsList.scrollTop = 0;
        eventsError.classList.add("hidden");
        eventsStatus.textContent = live ? "Loading live connections…" : "Loading connection attempts…";
        document.querySelector("#events-dialog-title").textContent = live ? "Live connections" : "Connection attempts";
        document.querySelector("#events-description").textContent = live
            ? "Open forwarded TCP connections. Names are supplied by clients; these are not confirmed Minecraft logins."
            : "Latest 20 login attempts for this proxy. Names are supplied by clients; these are not confirmed joins. Server-list pings are excluded.";
        eventsList.setAttribute("aria-label", live ? "Live player connections" : "Recent player connection attempts");
        document.querySelector("#events-domain").textContent = proxy.domain;
        eventsDialog.showModal();
        loadEvents();
    }

    document.querySelector("#close-events").addEventListener("click", () => eventsDialog.close());
    eventsDialog.addEventListener("close", () => {
        eventsRequest?.abort();
        eventsProxyId = null;
    });

    function validateDomain() {
        const message = domainField.validity.valid ? "" : domainField.validity.valueMissing
            ? "Enter a domain name."
            : "Enter a domain containing a dot, such as play.example.com.";
        domainError.textContent = message;
        domainError.classList.toggle("hidden", !message);
        domainField.setAttribute("aria-invalid", String(Boolean(message)));
    }

    domainField.addEventListener("invalid", (event) => {
        event.preventDefault();
        validateDomain();
        domainField.focus();
    });
    domainField.addEventListener("input", () => {
        if (domainField.hasAttribute("aria-invalid")) {
            validateDomain();
        }
    });

    function updateDestinationWarning() {
        const address = form.elements.destinationHost.value.trim();
        const octets = address.split(".");
        const isIpv4 = octets.length === 4 && octets.every((octet) => /^\d{1,3}$/.test(octet) && Number(octet) <= 255);
        const lastOctet = isIpv4 ? Number(octets[3]) : null;
        destinationWarning.classList.toggle("hidden", lastOctet !== 1 && lastOctet !== 255);
        destinationWarning.textContent = lastOctet === 1
            ? "An address ending in .1 is often used by a router or gateway. Check that this is your Minecraft server."
            : lastOctet === 255 ? "On a /24 network, .255 is the broadcast address. Check your subnet before using it as a server address." : "";
    }

    form.elements.destinationHost.addEventListener("input", updateDestinationWarning);

    function openEditor(proxy = null) {
        editingProxyId = proxy?.id ?? null;
        form.reset();
        domainField.removeAttribute("aria-invalid");
        domainError.textContent = "";
        domainError.classList.add("hidden");
        if (proxy) {
            form.elements.domain.value = proxy.domain;
            form.elements.destinationHost.value = proxy.destinationHost;
            form.elements.destinationPort.value = proxy.destinationPort;
        }
        document.querySelector("#proxy-dialog-title").textContent = proxy ? "Edit proxy" : "Add proxy";
        form.querySelector('button[type="submit"]').textContent = proxy ? "Save changes" : "Save proxy";
        error.classList.add("hidden");
        updateDestinationWarning();
        dialog.showModal();
    }

    async function copyDomain(domain) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(domain);
            } else {
                // The dashboard may be served over plain HTTP on a LAN, where Clipboard API is unavailable.
                const field = document.createElement("textarea");
                field.value = domain;
                field.className = "fixed opacity-0";
                field.setAttribute("aria-label", "Domain to copy");
                document.body.append(field);
                const focused = document.activeElement;
                try {
                    field.select();
                    if (!document.execCommand("copy")) {
                        throw new Error("Copy unavailable. Select and copy the domain manually.");
                    }
                } finally {
                    field.remove();
                    focused?.focus();
                }
            }
            feedback.textContent = `Copied ${domain}`;
        } catch (failure) {
            showError(failure);
        }
    }

    function updateStatuses(proxies) {
        for (const proxy of proxies) {
            const cell = statuses.get(proxy.id);
            if (!cell) {
                continue;
            }
            cell.textContent = !proxy.isActive ? "Disabled" : proxy.isReachable === null ? "Checking…" : proxy.isReachable ? "Active" : "Unreachable";
            cell.className = proxy.isActive && proxy.isReachable ? "p-0 text-accent md:px-6 md:py-4" : "p-0 text-muted md:px-6 md:py-4";
            cell.setAttribute("aria-label", `Backend status: ${cell.textContent}`);
            cell.title = proxy.lastCheckedAt ? `TCP check: ${new Date(proxy.lastCheckedAt).toLocaleString()}` : "Waiting for the first TCP check";
            const count = connectionCounts.get(proxy.id);
            count.textContent = String(proxy.liveConnectionCount);
            count.setAttribute("aria-label", `${proxy.liveConnectionCount} live connections`);
        }
    }

    async function load() {
        activeRequest?.abort();
        const request = new AbortController();
        activeRequest = request;
        try {
            const proxies = await api(`/proxies?search=${encodeURIComponent(search.value)}`, { signal: request.signal });
            const configuration = JSON.stringify([search.value, proxies.map((proxy) => [proxy.id, proxy.domain, proxy.destinationHost, proxy.destinationPort, proxy.isActive])]);
            if (configuration === renderedConfiguration) {
                updateStatuses(proxies);
                return;
            }
            renderedConfiguration = configuration;
            statuses.clear();
            connectionCounts.clear();
            list.replaceChildren();
            if (proxies.length === 0) {
                const row = list.insertRow();
                row.className = "proxy-empty";
                const cell = row.insertCell();
                cell.colSpan = 5;
                cell.className = "px-6 py-12 text-center text-muted";
                cell.textContent = search.value.trim() ? "No matching proxies." : "No proxies configured. Use + to add your first proxy.";
            }
            for (const proxy of proxies) {
                const row = list.insertRow();
                row.className = "proxy-row";
                const domain = row.insertCell();
                domain.className = "proxy-domain p-0 font-medium md:px-6 md:py-4";
                const copy = document.createElement("button");
                copy.type = "button";
                copy.className = "max-w-full cursor-copy [overflow-wrap:anywhere] text-left text-accent underline decoration-dotted underline-offset-4 hover:decoration-solid";
                copy.textContent = proxy.domain;
                copy.title = "Copy domain";
                copy.setAttribute("aria-label", `Copy ${proxy.domain}`);
                copy.addEventListener("click", () => copyDomain(proxy.domain));
                domain.append(copy);
                const destination = row.insertCell();
                destination.className = "proxy-destination p-0 font-mono text-xs text-secondary md:px-6 md:py-4";
                const host = proxy.destinationHost.includes(":") ? `[${proxy.destinationHost}]` : proxy.destinationHost;
                const destinationLabel = document.createElement("span");
                destinationLabel.className = "mb-1 block font-sans text-xs font-medium text-muted md:hidden";
                destinationLabel.textContent = "Destination";
                const address = document.createElement("span");
                address.className = "[overflow-wrap:anywhere]";
                address.textContent = `${host}:${proxy.destinationPort}`;
                destination.append(destinationLabel, address);
                const status = row.insertCell();
                statuses.set(proxy.id, status);
                const countCell = row.insertCell();
                countCell.className = "p-0 text-right md:px-4 md:py-4";
                countCell.title = "Live forwarded connections; refreshes every 5 seconds";
                const countLabel = document.createElement("span");
                countLabel.className = "mb-1 block text-xs text-muted md:hidden";
                countLabel.textContent = "Connections";
                const count = document.createElement("span");
                count.className = "proxy-connection-count font-medium tabular-nums text-body";
                connectionCounts.set(proxy.id, count);
                countCell.append(countLabel, count);
                const actions = row.insertCell();
                actions.className = "proxy-actions p-0 pt-3 text-right md:px-6 md:py-4";
                const buttons = document.createElement("div");
                buttons.className = "flex flex-wrap justify-end gap-2 md:flex-nowrap";
                const connections = document.createElement("button");
                connections.type = "button";
                connections.className = "button-secondary size-11 shrink-0 p-0 text-accent md:size-9";
                connections.innerHTML = `
                    <svg aria-hidden="true" class="size-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">
                        <rect x="3" y="3" width="18" height="14" rx="2" />
                        <path d="M8 21h8m-4-4v4M6 10h3l2-4 2 8 2-4h3" />
                    </svg>
                `;
                connections.title = "Live connections";
                connections.setAttribute("aria-label", `Live connections for ${proxy.domain}`);
                connections.addEventListener("click", () => openEvents(proxy, true));
                const history = document.createElement("button");
                history.type = "button";
                history.className = "button-secondary size-11 shrink-0 p-0 md:size-9";
                history.innerHTML = `
                    <svg aria-hidden="true" class="size-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">
                        <circle cx="12" cy="12" r="9" />
                        <path d="M12 7v5l3 2" />
                    </svg>
                `;
                history.title = "Connection history";
                history.setAttribute("aria-label", `Connection history for ${proxy.domain}`);
                history.addEventListener("click", () => openEvents(proxy));
                const edit = document.createElement("button");
                edit.type = "button";
                edit.className = "button-secondary size-11 shrink-0 p-0 md:size-9";
                edit.innerHTML = `
                    <svg aria-hidden="true" class="size-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">
                        <path d="m16 3 5 5M4 20l5-1L21 7a3.54 3.54 0 0 0-5-5L4 14z" />
                    </svg>
                `;
                edit.title = "Edit proxy";
                edit.setAttribute("aria-label", `Edit ${proxy.domain}`);
                edit.addEventListener("click", () => openEditor(proxy));
                const remove = document.createElement("button");
                remove.type = "button";
                remove.className = "button-secondary size-11 shrink-0 p-0 text-danger md:size-9";
                remove.innerHTML = `
                    <svg aria-hidden="true" class="size-4" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">
                        <path d="M3 6h18M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M5 6l1 14a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1l1-14M10 10v7m4-7v7" />
                    </svg>
                `;
                remove.title = "Delete proxy";
                remove.setAttribute("aria-label", `Delete ${proxy.domain}`);
                remove.addEventListener("click", async () => {
                    edit.disabled = true;
                    remove.disabled = true;
                    try {
                        await api(`/proxies/${proxy.id}`, { method: "DELETE" });
                        feedback.textContent = `Deleted ${proxy.domain}.`;
                        await load();
                    } catch (failure) {
                        showError(failure);
                    } finally {
                        edit.disabled = false;
                        remove.disabled = false;
                    }
                });
                buttons.append(connections, history, edit, remove);
                actions.append(buttons);
            }
            updateStatuses(proxies);
        } catch (failure) {
            if (failure.name !== "AbortError") {
                showError(failure);
            }
        } finally {
            if (activeRequest === request) {
                activeRequest = undefined;
            }
        }
    }

    document.querySelector("#proxy-search").addEventListener("submit", (event) => {
        event.preventDefault();
        clearTimeout(searchTimer);
        load();
    });
    search.addEventListener("input", () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(load, 200);
    });
    document.querySelector("#add-proxy").addEventListener("click", () => openEditor());
    document.querySelector("#cancel-proxy").addEventListener("click", () => dialog.close());
    handleForm("#proxy-create", async (data) => {
        const request = Object.fromEntries(data);
        request.destinationPort = Number(request.destinationPort);
        await api(editingProxyId === null ? "/proxies" : `/proxies/${editingProxyId}`, {
            method: editingProxyId === null ? "POST" : "PUT", body: JSON.stringify(request)
        });
        dialog.close();
        feedback.textContent = editingProxyId === null ? "Proxy added and ready for connections." : "Proxy updated. Players on this route can reconnect now.";
        await load();
    }, error);
    load();
    setInterval(() => {
        if (document.hidden) {
            return;
        }
        if (!activeRequest) {
            load();
        }
        if (eventsDialog.open && !showingLiveConnections && !eventsRequest) {
            loadEvents();
        }
    }, 5000);
    setInterval(() => {
        if (!document.hidden && eventsDialog.open && showingLiveConnections && !eventsRequest) {
            loadEvents();
        }
    }, 2000);
}

if (page !== "error" && page !== "public") {
    initialize().catch((error) => showError(error));
}
