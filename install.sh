#!/usr/bin/env bash
set -Eeuo pipefail
umask 027

usage() {
    cat <<'HELP'
ProxyCraft installer for Debian 13 (amd64/arm64, systemd).

  curl -fsSL https://raw.githubusercontent.com/DragoQC/ProxyCraft/main/install.sh | bash

Run as root inside your Debian 13 VM/LXC. Downloads DragoQC/ProxyCraft from GitHub.

Options:
  --ref REF         Branch, tag, or commit to install (default: main)
  --port PORT       Dashboard HTTP port (default: 8080; keeps existing on update)
  --help            Show this help without changing the system

Minecraft defaults to TCP 25565. Authentication is configured in App Settings.
Rerun to update; saved routes, account, settings, API key, and cookie keys survive.
HELP
}

fail() { printf 'ProxyCraft: %s\n' "$*" >&2; exit 1; }
value_required() { [[ $# -ge 2 && -n $2 && $2 != --* ]] || fail "Missing value for $1."; }

repository='DragoQC/ProxyCraft'
revision='main'
web_port=''
while (($#)); do
    case "$1" in
        --ref) value_required "$@"; revision=$2; shift 2 ;;
        --port) value_required "$@"; web_port=$2; shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Unknown option: $1. Use --help." ;;
    esac
done
[[ $revision =~ ^[a-zA-Z0-9_][a-zA-Z0-9._/-]*$ ]] || fail 'Invalid branch, tag, or commit.'
[[ $revision != *..* && $revision != *//* && $revision != */ && $revision != *. ]] || fail 'Invalid branch, tag, or commit.'
if [[ -n $web_port ]]; then
    [[ $web_port =~ ^[0-9]{1,5}$ ]] && ((10#$web_port >= 1 && 10#$web_port <= 65535)) || fail 'Dashboard port must be 1–65535.'
    web_port=$((10#$web_port))
fi

archive_url="https://codeload.github.com/$repository/tar.gz/$revision"

[[ $EUID -eq 0 ]] || fail 'Run this installer as root (or with sudo).'
# /etc/os-release is the distribution-provided shell-format OS metadata.
. /etc/os-release
[[ ${ID:-} == debian && ${VERSION_ID:-} == 13 ]] || fail 'This installer supports Debian 13 only. Other distributions have not been tested.'
case "$(dpkg --print-architecture)" in amd64|arm64) ;; *) fail 'Only amd64 and arm64 are supported.' ;; esac
[[ -d /run/systemd/system ]] || fail 'A running systemd system is required. For Docker, use the Dockerfile/compose.yaml instead.'

app_directory=/opt/proxycraft
data_directory=/var/lib/proxycraft
config_directory=/etc/proxycraft
environment_file=$config_directory/proxycraft.env
[[ ! -e $app_directory/current || -L $app_directory/current ]] || fail '/opt/proxycraft/current already exists and is not an installer-managed symlink.'

if [[ -z $web_port && -f $environment_file ]]; then
    web_port=$(sed -n 's/^ASPNETCORE_HTTP_PORTS=//p' "$environment_file")
fi
web_port=${web_port:-8080}
[[ $web_port =~ ^[0-9]{1,5}$ ]] && ((10#$web_port >= 1 && 10#$web_port <= 65535)) || fail 'Invalid existing dashboard port. Supply --port PORT.'
web_port=$((10#$web_port))

work_directory=$(mktemp -d /tmp/proxycraft-install.XXXXXXXX)
trap 'rm -rf -- "$work_directory"' EXIT
export DEBIAN_FRONTEND=noninteractive
printf 'Installing build prerequisites…\n'
apt-get update < /dev/null
apt-get install -y --no-install-recommends ca-certificates curl tar gzip < /dev/null
if ! /usr/bin/dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    curl -fsSL --retry 3 --proto '=https' https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -o "$work_directory/microsoft.deb"
    dpkg --force-confdef --force-confold -i "$work_directory/microsoft.deb" < /dev/null
    apt-get update < /dev/null
    apt-get install -y --no-install-recommends dotnet-sdk-10.0 < /dev/null
fi

getent group proxycraft >/dev/null || groupadd --system proxycraft
if ! id -u proxycraft >/dev/null 2>&1; then
    useradd --system --gid proxycraft --home-dir "$data_directory" --no-create-home --shell /usr/sbin/nologin proxycraft
fi
[[ $(id -u proxycraft) -ne 0 ]] || fail 'The proxycraft account must not be root.'
install -d -o proxycraft -g proxycraft -m 0700 "$data_directory"
install -d -m 0755 "$app_directory/releases"
install -d -o root -g proxycraft -m 0750 "$config_directory"
mkdir -p "$work_directory/source"

printf 'Downloading %s at %s…\n' "$repository" "$revision"
curl -fsSL --retry 3 --proto '=https' "$archive_url" -o "$work_directory/source.tar.gz"
tar -xzf "$work_directory/source.tar.gz" --strip-components=1 -C "$work_directory/source"
[[ -f $work_directory/source/ProxyCraft.csproj && -f $work_directory/source/packages.lock.json ]] || fail 'Downloaded source is not a ProxyCraft project.'
install -d -m 0700 "$work_directory/runtime"
chown -R proxycraft:proxycraft "$work_directory"
# Builds run as the service account, never as root. Build tools/cache stay temporary.
build_dotnet() {
    runuser -u proxycraft -- env XDG_RUNTIME_DIR="$work_directory/runtime" \
        DOTNET_CLI_HOME="$work_directory/home" NUGET_PACKAGES="$work_directory/nuget" \
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 /usr/bin/dotnet "$@" < /dev/null
}
printf 'Building ProxyCraft…\n'
cd "$work_directory/source"
build_dotnet restore ProxyCraft.csproj --locked-mode
build_dotnet publish ProxyCraft.csproj --configuration Release --no-restore --no-self-contained -p:UseAppHost=false --output "$work_directory/publish"
[[ -f $work_directory/publish/ProxyCraft.dll ]] || fail 'Publish did not produce ProxyCraft.dll.'

previous_release=$(readlink "$app_directory/current" || true)
if [[ -f $environment_file ]]; then cp -p "$environment_file" "$work_directory/previous.env"; fi
if [[ ! -f $environment_file ]]; then
    cat > "$environment_file" <<ENVIRONMENT
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_HTTP_PORTS=$web_port
Storage__Directory=$data_directory
DataProtection__KeysDirectory=$data_directory/keys
Http__RedirectToHttps=false
HOME=$data_directory
ENVIRONMENT
else
    if grep -q '^ASPNETCORE_HTTP_PORTS=' "$environment_file"; then
        sed -i "s/^ASPNETCORE_HTTP_PORTS=.*/ASPNETCORE_HTTP_PORTS=$web_port/" "$environment_file"
    else
        printf '\nASPNETCORE_HTTP_PORTS=%s\n' "$web_port" >> "$environment_file"
    fi
fi
chown root:proxycraft "$environment_file"
chmod 0640 "$environment_file"

cat > /etc/systemd/system/proxycraft.service <<'UNIT'
[Unit]
Description=ProxyCraft Minecraft proxy
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=proxycraft
Group=proxycraft
WorkingDirectory=/opt/proxycraft/current
EnvironmentFile=/etc/proxycraft/proxycraft.env
ExecStart=/usr/bin/dotnet /opt/proxycraft/current/ProxyCraft.dll
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
NoNewPrivileges=true
UMask=0077
SyslogIdentifier=proxycraft

[Install]
WantedBy=multi-user.target
UNIT
chmod 0644 /etc/systemd/system/proxycraft.service

release_name="$(date -u +%Y%m%dT%H%M%SZ)-$RANDOM"
release_directory=$app_directory/releases/$release_name
install -d -m 0755 "$release_directory"
cp -a "$work_directory/publish/." "$release_directory/"
chown -R root:root "$release_directory"
chmod -R u=rwX,go=rX "$release_directory"
ln -s "releases/$release_name" "$app_directory/.current-$$"
mv -Tf "$app_directory/.current-$$" "$app_directory/current"
systemctl daemon-reload
systemctl enable proxycraft.service

wait_for_app() {
    for ((attempt = 0; attempt < 30; attempt++)); do
        if systemctl is-active --quiet proxycraft.service \
            && curl -fsS --max-time 2 "http://127.0.0.1:$web_port/logo.svg" -o /dev/null 2>/dev/null; then return 0; fi
        sleep 1
    done
    return 1
}
if ! { systemctl restart proxycraft.service && wait_for_app; }; then
    if [[ -n $previous_release ]]; then
        ln -s "$previous_release" "$app_directory/.current-$$"
        mv -Tf "$app_directory/.current-$$" "$app_directory/current"
        if [[ -f $work_directory/previous.env ]]; then cp -p "$work_directory/previous.env" "$environment_file"; fi
        systemctl restart proxycraft.service || true
        printf 'Restored the previous release and environment.\n' >&2
    else
        systemctl stop proxycraft.service || true
    fi
    fail 'Startup failed. Inspect: journalctl -u proxycraft -n 50 --no-pager'
fi
printf '\nProxyCraft is running.\nDashboard: http://<server-ip>:%s\nMinecraft: TCP 25565 (default)\nData: %s\nService: systemctl status proxycraft\n' "$web_port" "$data_directory"
