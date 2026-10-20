"""Exercise installer decisions in a disposable filesystem with mocked host tools."""
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parent.parent

MOCK = r'''#!/usr/bin/env python3
import os
from pathlib import Path
import shutil
import subprocess
import sys

name = Path(sys.argv[0]).name
args = sys.argv[1:]
root = Path(os.environ["INSTALL_TEST_ROOT"])
with (root / "commands.log").open("a") as log:
    log.write(name + " " + " ".join(args) + "\n")
if name == "dpkg":
    if args == ["--print-architecture"]: print(os.environ.get("INSTALL_TEST_ARCH", "amd64"))
elif name == "dotnet":
    if args == ["--list-sdks"]:
        if not os.environ.get("INSTALL_TEST_NO_SDK"): print("10.0.401 [/usr/share/dotnet/sdk]")
    elif args[0] == "restore":
        assert sys.stdin.read() == "", "Build tools must not consume a piped installer."
        runtime = Path(os.environ["XDG_RUNTIME_DIR"])
        assert runtime.is_dir() and runtime.stat().st_mode & 0o777 == 0o700
        assert runtime.parent.name.startswith("proxycraft-install."), "Builds must not inherit root's runtime directory."
        assert Path("ProxyCraft.csproj").is_file()
        assert not any(Path(name).exists() for name in ("App_Data", ".git", ".tools", ".env"))
        assert "--locked-mode" in args
        if os.environ.get("INSTALL_TEST_BUILD_FAIL"): sys.exit(1)
    elif args[0] == "publish":
        target = Path(args[args.index("--output") + 1])
        target.mkdir()
        (target / "ProxyCraft.dll").write_text(os.environ.get("INSTALL_TEST_RELEASE", "one"))
elif name == "runuser":
    assert args[:3] == ["-u", "proxycraft", "--"]
    os.execvpe(args[3], args[3:], os.environ)
elif name == "install":
    kept = []
    index = 0
    while index < len(args):
        if args[index] in ("-o", "-g"): index += 2
        else:
            kept.append(args[index])
            index += 1
    sys.exit(subprocess.call(["/usr/bin/install", *kept]))
elif name == "id":
    print("10001")
elif name == "curl":
    url = next(arg for arg in args if arg.startswith(("http://", "https://")))
    if url.startswith("https://codeload.github.com/"):
        if os.environ.get("INSTALL_TEST_DOWNLOAD_FAIL"): sys.exit(22)
        shutil.copyfile(root / "source.tar.gz", args[args.index("-o") + 1])
    elif url.startswith("https://packages.microsoft.com/"):
        Path(args[args.index("-o") + 1]).write_bytes(b"mock package feed")
elif name == "systemctl" and args[0] == "restart":
    if (root / "opt/proxycraft/current/ProxyCraft.dll").read_text() == "bad-start": sys.exit(1)
'''


def verify():
    with tempfile.TemporaryDirectory(prefix="proxycraft-installer-test-") as temporary:
        directory = Path(temporary)
        tools = directory / "tools"
        tools.mkdir()
        for name in ("dpkg", "apt-get", "dotnet", "getent", "groupadd", "useradd", "id", "install", "chown", "runuser", "curl", "systemctl"):
            tool = tools / name
            tool.write_text(MOCK)
            tool.chmod(0o755)
        (directory / "os-release").write_text('ID=debian\nVERSION_ID="13"\n')
        (directory / "run/systemd/system").mkdir(parents=True)
        (directory / "etc/systemd/system").mkdir(parents=True)
        source = directory / "source checkout"
        source.mkdir()
        (source / "ProxyCraft.csproj").write_text("<Project />")
        (source / "packages.lock.json").write_text("{}")
        with tarfile.open(directory / "source.tar.gz", "w:gz") as archive:
            for name in ("ProxyCraft.csproj", "packages.lock.json"):
                archive.add(source / name, arcname=f"ProxyCraft-snapshot/{name}")

        script = (ROOT / "install.sh").read_text()
        script = script.replace("[[ $EUID -eq 0 ]]", "true")
        script = script.replace("/etc/os-release", str(directory / "os-release"))
        script = script.replace("/run/systemd/system", str(directory / "run/systemd/system"))
        script = script.replace("/etc/systemd/system", str(directory / "etc/systemd/system"))
        for path in ("/opt/proxycraft", "/var/lib/proxycraft", "/etc/proxycraft"):
            script = script.replace(path, str(directory / path.lstrip("/")))
        script = script.replace("/usr/bin/dotnet", str(tools / "dotnet"))
        installer = directory / "install.sh"
        installer.write_text(script)
        environment = {**os.environ, "PATH": str(tools) + os.pathsep + os.environ["PATH"],
                       "INSTALL_TEST_ROOT": str(directory), "XDG_RUNTIME_DIR": str(directory / "root-runtime")}
        log = directory / "commands.log"

        def run(*arguments, success=True, piped=False, **settings):
            command = ["bash", "-s", "--", *arguments] if piped else ["bash", str(installer), *arguments]
            result = subprocess.run(command, env={**environment, **settings}, input=script if piped else None,
                                    capture_output=True, text=True, timeout=20)
            assert (result.returncode == 0) == success, result.stdout + result.stderr
            return result

        for arguments in (("--unknown",), ("--port",), ("--source", str(source)), ("--repository", "https://github.com/example/ProxyCraft"), ("--ref",),
                          ("--port", "0"), ("--port", "65536"), ("--port", "oops"),
                          ("--ref", "../../bad?"), ("--ref", "branch/../../other")):
            run(*arguments, success=False)
        assert not log.exists(), "Invalid options must fail before host operations."
        run(success=False, INSTALL_TEST_ARCH="armhf")
        assert "apt-get" not in log.read_text()
        log.unlink()
        (directory / "os-release").write_text('ID=ubuntu\nVERSION_ID="24.04"\n')
        run(success=False)
        assert not log.exists()
        (directory / "os-release").write_text('ID=debian\nVERSION_ID="13"\n')
        print("PASS: invalid input, unsupported distributions, and unsupported architectures fail before installation.")

        run(piped=True)
        app = directory / "opt/proxycraft"
        data = directory / "var/lib/proxycraft"
        configuration = directory / "etc/proxycraft/proxycraft.env"
        assert (app / "current").is_symlink() and (app / "current/ProxyCraft.dll").read_text() == "one"
        assert "ASPNETCORE_HTTP_PORTS=8080" in configuration.read_text()
        assert "DataProtection__KeysDirectory=" in configuration.read_text()
        assert stat.S_IMODE(configuration.stat().st_mode) == 0o640
        unit = (directory / "etc/systemd/system/proxycraft.service").read_text()
        assert "User=proxycraft" in unit and "Group=proxycraft" in unit and "Restart=on-failure" in unit
        assert "packages.microsoft.com" not in log.read_text(), "An existing .NET 10 SDK must be reused."
        assert "https://codeload.github.com/DragoQC/ProxyCraft/tar.gz/main" in log.read_text()
        (data / "proxycraft.db").write_bytes(b"existing database")
        (data / "settings.json").write_text('{"authenticationEnabled":true}')
        (data / "keys").mkdir()
        (data / "keys/key.xml").write_text("existing cookie key")
        configuration.write_text(configuration.read_text() + "CUSTOM_SETTING=keep\n")
        print("PASS: piping install.sh with no arguments downloads DragoQC/ProxyCraft, builds as the service account, and installs a persistent systemd deployment.")

        run(piped=True, INSTALL_TEST_RELEASE="two")
        current = (app / "current").readlink()
        preserved = configuration.read_text()
        assert "ASPNETCORE_HTTP_PORTS=8080" in preserved and "CUSTOM_SETTING=keep" in preserved
        assert (app / "current/ProxyCraft.dll").read_text() == "two"
        run("--port", "8086", piped=True, success=False, INSTALL_TEST_DOWNLOAD_FAIL="1")
        assert (app / "current").readlink() == current and configuration.read_text() == preserved
        run("--port", "8086", piped=True, success=False, INSTALL_TEST_BUILD_FAIL="1")
        assert (app / "current").readlink() == current and configuration.read_text() == preserved
        run("--port", "8086", piped=True, success=False, INSTALL_TEST_RELEASE="bad-start")
        assert (app / "current").readlink() == current and configuration.read_text() == preserved
        assert (data / "proxycraft.db").read_bytes() == b"existing database"
        assert (data / "settings.json").read_text() == '{"authenticationEnabled":true}'
        assert (data / "keys/key.xml").read_text() == "existing cookie key"
        print("PASS: updates preserve data/settings/keys/ports; failed downloads/builds leave the active release alone; failed startup rolls back.")

        run("--ref", "release/next", "--port", "8087",
            piped=True, INSTALL_TEST_NO_SDK="1", INSTALL_TEST_RELEASE="downloaded")
        commands = log.read_text()
        assert "https://codeload.github.com/DragoQC/ProxyCraft/tar.gz/release/next" in commands
        assert "https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb" in commands
        assert "dotnet-sdk-10.0" in commands and "ASPNETCORE_HTTP_PORTS=8087" in configuration.read_text()
        assert (app / "current/ProxyCraft.dll").read_text() == "downloaded"
        print("PASS: remote archive installation, Microsoft SDK bootstrap, and explicit dashboard-port updates.")


if __name__ == "__main__":
    verify()
