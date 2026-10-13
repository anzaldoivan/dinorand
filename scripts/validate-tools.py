#!/usr/bin/env python3
"""Validate the exact indexed tools gitlink before scanning or executing child code."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

URL = "https://github.com/anzaldoivan/dino-tools.git"

def child_environment():
    environment = os.environ.copy()
    local_names = subprocess.check_output(["git", "rev-parse", "--local-env-vars"], text=True).splitlines()
    for name in local_names:
        environment.pop(name, None)
    return environment

def run(root, *args, isolated=False):
    environment = child_environment() if isolated else None
    return subprocess.check_output(["git", "-C", str(root), *args], text=True, env=environment).strip()

def validate(root):
    tools = root / "tools"
    entry = run(root, "ls-files", "--stage", "--", "tools").split()
    if len(entry) != 4 or entry[0] != "160000" or entry[2] != "0":
        raise ValueError("tools must be a single resolved gitlink")
    if run(root, "config", "--file", ".gitmodules", "--get", "submodule.tools.path") != "tools":
        raise ValueError("tools submodule path mismatch")
    if run(root, "config", "--file", ".gitmodules", "--get", "submodule.tools.url") != URL:
        raise ValueError("tools submodule URL mismatch")
    if not (tools / ".git").exists():
        raise ValueError("tools submodule is missing; run git submodule update --init tools")
    if Path(run(tools, "rev-parse", "--show-toplevel", isolated=True)).resolve() != tools.resolve():
        raise ValueError("tools is not an independent initialized repository")
    actual = run(tools, "rev-parse", "HEAD", isolated=True)
    if actual != entry[1]:
        raise ValueError(f"tools commit mismatch: indexed {entry[1]}, checkout {actual}")
    if run(tools, "status", "--porcelain", "--untracked-files=all", isolated=True):
        raise ValueError("tools checkout is dirty")
    print(f"Verified tools gitlink {actual}")
    return tools

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scan-only", action="store_true")
    parser.add_argument("--real-install", action="store_true", help="Opt in to configured read-only fixture tests")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    try:
        tools = validate(root)
        environment = child_environment()
        subprocess.run(["python3", "scripts/check-provenance.py"], cwd=tools, check=True, env=environment)
        subprocess.run(["python3", "scripts/check-lockfiles.py"], cwd=tools, check=True, env=environment)
        subprocess.run(["bash", "scripts/check-no-copyrighted-files.sh"], cwd=tools, check=True, env=environment)
        if not args.scan_only:
            commands = [
                ["dotnet", "restore", "DinoTools.sln", "--locked-mode"],
                ["dotnet", "build", "DinoTools.sln", "-c", "Release", "--no-restore"],
                ["dotnet", "test", "DinoTools.sln", "-c", "Release", "--no-build"],
                ["python3", "-m", "unittest", "discover", "-s", "tests", "-v"],
                ["python3", "-m", "unittest", "discover", "-s", "dc2_re", "-v"],
            ]
            with tempfile.TemporaryDirectory(prefix="dino-tools-validation-") as temporary:
                execution_root = tools
                if not args.real_install:
                    # Prove the standalone clone contract and prevent parent-private donor
                    # fixtures or build output from influencing the required synthetic gate.
                    execution_root = Path(temporary) / "checkout"
                    subprocess.run(
                        ["git", "clone", "--quiet", "--no-checkout", "--no-hardlinks", str(tools), str(execution_root)],
                        check=True, env=environment,
                    )
                    subprocess.run(
                        ["git", "-C", str(execution_root), "checkout", "--quiet", "--detach", run(tools, "rev-parse", "HEAD", isolated=True)],
                        check=True, env=environment,
                    )
                    # Explicit unavailable fixture roots suppress ancestor .env discovery.
                    # These paths are never created; synthetic writes remain in test-owned temp dirs.
                    for game in ("DC1", "DC2"):
                        environment[f"DINORAND_{game}_DIR"] = str(Path(temporary) / game)
                    environment.pop("DINO_TOOLS_REQUIRE_PIPELINE", None)
                for command in commands:
                    subprocess.run(command, cwd=execution_root, check=True, env=environment)
    except (ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"tools validation failed: {error}")

if __name__ == "__main__":
    main()
