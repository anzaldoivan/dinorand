#!/usr/bin/env python3
"""Generate fail-closed notices from RID-specific publish graphs."""
from __future__ import annotations
import argparse, json, re, shutil, sys
from dataclasses import dataclass
from pathlib import Path
import xml.etree.ElementTree as ET
class NoticeError(RuntimeError): pass
@dataclass(frozen=True)
class PackageEvidence:
    name: str; version: str; source: str; license_expression: str | None; license_file: Path | None; notice_files: tuple[Path, ...]; license_source: str | None = None
def _child(metadata, name): return next((c for c in metadata if c.tag.rsplit("}", 1)[-1] == name), None)
def read_package_evidence(nuspec: Path, override=None, root: Path | None = None) -> PackageEvidence:
    try: tree = ET.parse(nuspec)
    except (OSError, ET.ParseError) as error: raise NoticeError(f"cannot parse {nuspec.name}: {error}") from error
    metadata = next((e for e in tree.iter() if e.tag.rsplit("}", 1)[-1] == "metadata"), None)
    if metadata is None: raise NoticeError(f"{nuspec.name}: missing NuGet metadata")
    def text(name):
        element = _child(metadata, name); return (element.text or "").strip() if element is not None else ""
    name, version, license_element = text("id"), text("version"), _child(metadata, "license")
    if not name or not version: raise NoticeError(f"{nuspec.name}: missing id/version")
    expression = None; license_file = None; license_source = None
    if license_element is None and override:
        if root is None: raise NoticeError(f"{name} {version}: override has no repository root")
        metadata_file = nuspec.parent / ".nupkg.metadata"
        try: restored_hash = json.loads(metadata_file.read_text(encoding="utf-8"))["contentHash"]
        except (OSError, KeyError, json.JSONDecodeError) as error: raise NoticeError(f"{name} {version}: restored package hash metadata is missing") from error
        if restored_hash != override.get("content_hash"):
            raise NoticeError(f"{name} {version}: curated license evidence does not match the restored package hash")
        license_file = (nuspec.parent / override.get("package_license_file", "")).resolve()
        if not license_file.is_file() or nuspec.parent.resolve() not in license_file.parents:
            raise NoticeError(f"{name} {version}: curated license file is missing or unsafe")
        import hashlib
        if hashlib.sha256(license_file.read_bytes()).hexdigest() != override.get("license_sha256"):
            raise NoticeError(f"{name} {version}: curated license file hash mismatch")
        for notice_name, notice_hash in override.get("required_notice_files", {}).items():
            notice_path = (nuspec.parent / notice_name).resolve()
            if not notice_path.is_file() or hashlib.sha256(notice_path.read_bytes()).hexdigest() != notice_hash:
                raise NoticeError(f"{name} {version}: required package notice is missing or mismatched")
        expression = override.get("license_expression")
        license_source = override.get("license_source")
        if not expression or not re.fullmatch(r"https://github\.com/dotnet/corefx/blob/[0-9a-f]{40}/LICENSE\.TXT", license_source or ""):
            raise NoticeError(f"{name} {version}: curated license source is not an exact official commit")
    elif license_element is None:
        raise NoticeError(f"{name} {version}: missing NuGet <license>; a license URL is insufficient")
    else:
        kind, value = (license_element.attrib.get("type") or "").lower(), (license_element.text or "").strip()
        if kind == "expression" and value: expression = value
        elif kind == "file" and value:
            license_file = (nuspec.parent / value).resolve()
            if not license_file.is_file() or nuspec.parent.resolve() not in license_file.parents: raise NoticeError(f"{name} {version}: declared license file missing or unsafe")
        else: raise NoticeError(f"{name} {version}: unsupported or empty license metadata")
    notices = tuple(sorted(p for p in nuspec.parent.rglob("*") if p.is_file() and re.fullmatch(r"(?i)(?:third[-_ ]party[-_ ]notices?|notices?)(?:\.[^.]+)?", p.name)))
    return PackageEvidence(name, version, f"https://www.nuget.org/packages/{name}/{version}", expression, license_file, notices, license_source)
def assert_safe_text(text: str) -> None:
    for pattern in (r"(?i)(?:^|[\s=])/(?:tmp|home|Users|root)/", r"(?i)[A-Z]:\\(?:Users|Windows|Temp)\\", r"(?i)\b(?:authorization|password|secret|token)\s*[:=]", r"\b(?:gh[opsu]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b", r"(?i)\.nuget[/\\]packages"):
        if re.search(pattern, text): raise NoticeError("generated notice contains a machine path or credential-like value")
def shipped_package_paths(assets_file: Path, rid: str) -> set[Path]:
    try: assets = json.loads(assets_file.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error: raise NoticeError(f"cannot read {assets_file}: {error}") from error
    target_name = next((name for name in assets.get("targets", {}) if name.endswith(f"/{rid}")), None)
    if target_name is None: raise NoticeError(f"{assets_file}: no {rid} target")
    roots = [Path(p) for p in assets.get("packageFolders", {})]
    if len(roots) != 1: raise NoticeError(f"{assets_file}: expected one explicit package root")
    result = set()
    for key, target in assets["targets"][target_name].items():
        library = assets.get("libraries", {}).get(key, {})
        has_payload = any(
            any(not asset.replace("\\", "/").endswith("/_._") for asset in target.get(field, {}))
            for field in ("runtime", "runtimeTargets", "native")
        )
        if library.get("type") == "package" and has_payload:
            package = roots[0] / library.get("path", "")
            if not package.is_dir(): raise NoticeError(f"{key}: restored package missing")
            result.add(package)
    framework = assets.get("project", {}).get("frameworks", {}).get("net8.0", {})
    runtime_names = {
        f"microsoft.netcore.app.runtime.{rid}",
        f"microsoft.netcore.app.host.{rid}",
    }
    for dependency in framework.get("downloadDependencies", []):
        if dependency.get("name", "").casefold() not in runtime_names: continue
        version = dependency.get("version", "").strip("[]").split(",", 1)[0].strip()
        package = roots[0] / dependency["name"].lower() / version
        if not package.is_dir(): raise NoticeError(f"{dependency['name']} {version}: runtime package missing")
        result.add(package)
    return result
def _copy(source, destination): destination.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(source, destination)
def generate(root: Path, rid: str, output: Path, assets_files: list[Path]):
    overrides_path = root / "scripts/release-license-overrides.json"
    overrides = json.loads(overrides_path.read_text(encoding="utf-8"))
    package_dirs = set()
    for assets in assets_files: package_dirs.update(shipped_package_paths(assets, rid))
    pairs = []
    for package in package_dirs:
        specs = sorted(package.glob("*.nuspec"))
        if len(specs) != 1: raise NoticeError(f"{package.name}: expected one nuspec")
        key = f"{package.parent.name}/{package.name}".lower()
        pairs.append((read_package_evidence(specs[0], overrides.get(key), root), package))
    pairs.sort(key=lambda pair: (pair[0].name.casefold(), pair[0].version))
    if not pairs: raise NoticeError(f"no shipped packages found for {rid}")
    output.mkdir(parents=True, exist_ok=True)
    for name in ("LICENSE", "LEGAL.md", "THIRD-PARTY-NOTICES.md"):
        if not (root / name).is_file(): raise NoticeError(f"required project notice missing: {name}")
        _copy(root / name, output / name)
    notices = output / "notices"
    if notices.exists(): raise NoticeError(f"{rid}: refusing to reuse an existing notices directory")
    licenses = notices / "PACKAGE-LICENSES"; licenses.mkdir(parents=True, exist_ok=True)
    runtime_pair = next(((e, p) for e, p in pairs if e.name.casefold() == f"microsoft.netcore.app.runtime.{rid}".casefold()), None)
    if runtime_pair is None: raise NoticeError(f"Microsoft.NETCore.App.Runtime.{rid} absent from publish graph")
    runtime_files = [p for p in runtime_pair[1].rglob("*") if p.is_file()]
    runtime_license = next((p for p in runtime_files if p.name.casefold() in {"license.txt", "license.md"}), None)
    runtime_notice = next((p for p in runtime_files if p.name.casefold() in {"thirdpartynotices.txt", "third-party-notices.txt"}), None)
    if not runtime_license or not runtime_notice: raise NoticeError(f"{runtime_pair[0].name}: runtime license/notices missing")
    _copy(runtime_license, notices / "DOTNET-RUNTIME-LICENSE.txt"); _copy(runtime_notice, notices / "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt")
    host_name = f"Microsoft.NETCore.App.Host.{rid}"
    if not any(e.name.casefold() == host_name.casefold() for e, _ in pairs):
        host = PackageEvidence(
            host_name,
            runtime_pair[0].version,
            f"https://www.nuget.org/packages/{host_name}/{runtime_pair[0].version}",
            "MIT",
            runtime_license,
            (runtime_notice,),
            "https://github.com/dotnet/runtime",
        )
        pairs.append((host, runtime_pair[1])); pairs.sort(key=lambda pair: (pair[0].name.casefold(), pair[0].version))
    rows = ["# Shipped Component Inventory", "", f"Runtime identifier: `{rid}`", "", "Generated from the RID-specific publish graph; build/test-only dependencies are excluded.", "", "| Component | Version | Source | Verified license metadata |", "|---|---:|---|---|"]
    for evidence, package in pairs:
        label = evidence.license_expression or f"package file `{evidence.license_file.name}`"
        if evidence.license_source: label += f" ([exact source]({evidence.license_source}))"
        rows.append(f"| {evidence.name} | {evidence.version} | {evidence.source} | {label} |"); safe = re.sub(r"[^A-Za-z0-9_.-]", "_", f"{evidence.name}-{evidence.version}")
        if evidence.license_file: _copy(evidence.license_file, licenses / f"{safe}-LICENSE{evidence.license_file.suffix}")
        for index, notice in enumerate(evidence.notice_files, 1): _copy(notice, licenses / f"{safe}-NOTICE-{index}{notice.suffix}")
    inventory = "\n".join(rows) + "\n"; assert_safe_text(inventory); (notices / "DEPENDENCY-INVENTORY.md").write_text(inventory, encoding="utf-8", newline="\n")
    return [e for e, _ in pairs]
def main(argv=None):
    parser = argparse.ArgumentParser(); parser.add_argument("--rid", required=True); parser.add_argument("--output", type=Path, required=True); parser.add_argument("--assets", type=Path, action="append", required=True); parser.add_argument("--repository", type=Path, default=Path.cwd()); args = parser.parse_args(argv)
    try: evidence = generate(args.repository.resolve(), args.rid, args.output.resolve(), [p.resolve() for p in args.assets])
    except NoticeError as error: print(f"release notices failed: {error}", file=sys.stderr); return 1
    print(f"release notices: {args.rid}: {len(evidence)} shipped packages verified"); return 0
if __name__ == "__main__": raise SystemExit(main())
