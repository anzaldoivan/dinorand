#!/usr/bin/env python3
"""Create or resume, verify, and publish one DinoRand GitHub release."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import sys
import time
from typing import Callable, Protocol
import urllib.error
import urllib.parse
import urllib.request

from release_validate import ValidationError, parse_tag


API_VERSION = "2022-11-28"
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
SHA256_PATTERN = re.compile(r"^(?P<digest>[0-9a-f]{64})  (?P<name>[^/\\]+)$")


class PublishError(RuntimeError):
    pass


class ApiError(PublishError):
    def __init__(self, message: str, status: int | None = None):
        super().__init__(message)
        self.status = status


class TransientApiError(ApiError):
    pass


@dataclass(frozen=True)
class LocalAsset:
    name: str
    path: Path
    size: int
    digest: str
    content_type: str


@dataclass(frozen=True)
class RemoteAsset:
    id: int
    name: str
    size: int
    digest: str | None
    state: str


@dataclass(frozen=True)
class ReleaseSnapshot:
    id: int
    tag_name: str
    name: str
    body: str
    draft: bool
    prerelease: bool
    upload_url: str
    assets: tuple[RemoteAsset, ...]


class ReleaseBackend(Protocol):
    def resolve_annotated_tag(self, tag: str) -> str: ...

    def find_release(self, tag: str) -> ReleaseSnapshot | None: ...

    def create_draft(
        self, tag: str, name: str, body: str, prerelease: bool
    ) -> ReleaseSnapshot: ...

    def refresh_release(self, release_id: int) -> ReleaseSnapshot: ...

    def upload_asset(
        self, release: ReleaseSnapshot, asset: LocalAsset, timeout: float
    ) -> None: ...

    def delete_asset(self, asset_id: int) -> None: ...

    def publish(self, release_id: int) -> ReleaseSnapshot: ...


def expected_asset_names(version: str) -> tuple[str, ...]:
    return (
        f"dinorand-v{version}-win-x64.zip",
        f"dinorand-v{version}-linux-x64.zip",
        f"dinorand-v{version}-osx-arm64.zip",
        "dino_crisis_1.apworld",
        "dino_crisis_2.apworld",
        "SHA256SUMS",
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _content_type(name: str) -> str:
    if name.endswith(".zip"):
        return "application/zip"
    if name == "SHA256SUMS":
        return "text/plain"
    return "application/octet-stream"


def load_local_assets(asset_dir: Path, version: str) -> tuple[LocalAsset, ...]:
    asset_dir = asset_dir.resolve()
    expected = expected_asset_names(version)
    try:
        entries = list(asset_dir.iterdir())
    except OSError as error:
        raise PublishError(f"cannot read release asset directory: {error}") from error

    actual = {entry.name for entry in entries}
    if actual != set(expected) or any(
        not entry.is_file() or entry.is_symlink() for entry in entries
    ):
        raise PublishError(
            "release asset directory must contain exactly "
            f"{list(expected)}; found {sorted(actual)}"
        )

    sums_path = asset_dir / "SHA256SUMS"
    try:
        lines = sums_path.read_text(encoding="ascii").splitlines()
    except (OSError, UnicodeError) as error:
        raise PublishError(f"cannot read SHA256SUMS: {error}") from error

    declared: dict[str, str] = {}
    for line in lines:
        match = SHA256_PATTERN.fullmatch(line)
        if not match or match.group("name") in declared:
            raise PublishError(f"invalid or duplicate SHA256SUMS line: {line!r}")
        declared[match.group("name")] = match.group("digest")

    checksum_names = set(expected) - {"SHA256SUMS"}
    if set(declared) != checksum_names:
        raise PublishError(
            "SHA256SUMS must cover exactly "
            f"{sorted(checksum_names)}; found {sorted(declared)}"
        )

    assets = []
    for name in expected:
        path = asset_dir / name
        digest = _sha256(path)
        if name != "SHA256SUMS" and declared[name] != digest:
            raise PublishError(f"checksum mismatch for {name}")
        assets.append(
            LocalAsset(
                name=name,
                path=path,
                size=path.stat().st_size,
                digest=digest,
                content_type=_content_type(name),
            )
        )
    return tuple(assets)


def _normalized_notes(value: str) -> str:
    return value.replace("\r\n", "\n").rstrip()


def _remote_assets(release: ReleaseSnapshot) -> dict[str, RemoteAsset]:
    assets: dict[str, RemoteAsset] = {}
    for asset in release.assets:
        if asset.name in assets:
            raise PublishError(f"release contains duplicate asset {asset.name}")
        assets[asset.name] = asset
    return assets


def _validate_metadata(
    release: ReleaseSnapshot,
    *,
    tag: str,
    prerelease: bool,
    notes: str,
) -> None:
    expected_name = f"DinoRand {tag}"
    if release.tag_name != tag:
        raise PublishError(
            f"release tag is {release.tag_name}, expected {tag}"
        )
    if release.name != expected_name:
        raise PublishError(
            f"release name is {release.name!r}, expected {expected_name!r}"
        )
    if release.prerelease != prerelease:
        raise PublishError(
            f"release prerelease={release.prerelease}, expected {prerelease}"
        )
    if _normalized_notes(release.body) != _normalized_notes(notes):
        raise PublishError("release notes differ from the validated curated notes")


def _validate_remote_set(
    release: ReleaseSnapshot,
    local_assets: tuple[LocalAsset, ...],
    *,
    require_complete: bool,
) -> dict[str, RemoteAsset]:
    remote = _remote_assets(release)
    local = {asset.name: asset for asset in local_assets}
    unexpected = set(remote) - set(local)
    if unexpected:
        raise PublishError(f"release contains unexpected assets: {sorted(unexpected)}")

    for name, existing in remote.items():
        expected = local[name]
        if existing.state != "uploaded":
            if not release.draft:
                raise PublishError(
                    f"published release asset {name} has state {existing.state}"
                )
            continue
        if (
            existing.size != expected.size
            or existing.digest != f"sha256:{expected.digest}"
        ):
            raise PublishError(
                f"uploaded asset {name} does not match local size and SHA-256"
            )

    if require_complete and set(remote) != set(local):
        missing = set(local) - set(remote)
        raise PublishError(f"release is missing assets: {sorted(missing)}")
    return remote


def _retry_pause(
    attempt: int, retry_delay: float, sleep: Callable[[float], None]
) -> None:
    if retry_delay > 0:
        sleep(retry_delay * (2 ** (attempt - 1)))


def _create_or_resume(
    backend: ReleaseBackend,
    *,
    tag: str,
    prerelease: bool,
    notes: str,
    attempts: int,
    retry_delay: float,
    sleep: Callable[[float], None],
) -> ReleaseSnapshot:
    release = backend.find_release(tag)
    if release is not None:
        print(f"release: resume id={release.id} draft={str(release.draft).lower()}")
        return release

    for attempt in range(1, attempts + 1):
        print(f"release: create draft attempt {attempt}/{attempts}")
        try:
            return backend.create_draft(
                tag, f"DinoRand {tag}", notes, prerelease
            )
        except ApiError as error:
            release = backend.find_release(tag)
            if release is not None:
                print(f"release: reconciled created draft id={release.id}")
                return release
            if not isinstance(error, TransientApiError) or attempt == attempts:
                raise PublishError(f"could not create draft: {error}") from error
            _retry_pause(attempt, retry_delay, sleep)
    raise AssertionError("unreachable")


def _delete_incomplete(
    backend: ReleaseBackend,
    release: ReleaseSnapshot,
    asset: RemoteAsset,
    *,
    attempts: int,
    retry_delay: float,
    sleep: Callable[[float], None],
) -> ReleaseSnapshot:
    if asset.state != "starter":
        raise PublishError(
            f"refusing to delete {asset.name} with unexpected state "
            f"{asset.state}; only starter assets are safe to retry"
        )
    for attempt in range(1, attempts + 1):
        print(
            f"asset: delete incomplete {asset.name} "
            f"attempt {attempt}/{attempts}"
        )
        try:
            backend.delete_asset(asset.id)
        except ApiError as error:
            release = backend.refresh_release(release.id)
            if asset.name not in _remote_assets(release):
                return release
            if not isinstance(error, TransientApiError) or attempt == attempts:
                raise PublishError(
                    f"could not delete incomplete asset {asset.name}: {error}"
                ) from error
            _retry_pause(attempt, retry_delay, sleep)
            continue

        release = backend.refresh_release(release.id)
        if asset.name not in _remote_assets(release):
            return release
        if attempt < attempts:
            _retry_pause(attempt, retry_delay, sleep)
    raise PublishError(
        f"incomplete asset {asset.name} still exists after {attempts} attempts"
    )


def _upload_one(
    backend: ReleaseBackend,
    release: ReleaseSnapshot,
    asset: LocalAsset,
    local_assets: tuple[LocalAsset, ...],
    *,
    attempts: int,
    upload_timeout: float,
    retry_delay: float,
    sleep: Callable[[float], None],
) -> ReleaseSnapshot:
    last_error: BaseException | None = None
    for attempt in range(1, attempts + 1):
        print(f"asset: upload {asset.name} attempt {attempt}/{attempts}")
        try:
            backend.upload_asset(release, asset, upload_timeout)
            last_error = None
        except ApiError as error:
            last_error = error

        release = backend.refresh_release(release.id)
        remote = _validate_remote_set(
            release, local_assets, require_complete=False
        )
        existing = remote.get(asset.name)
        if existing is not None and existing.state == "uploaded":
            print(f"asset: verified {asset.name}")
            return release
        if existing is not None:
            release = _delete_incomplete(
                backend,
                release,
                existing,
                attempts=attempts,
                retry_delay=retry_delay,
                sleep=sleep,
            )

        if last_error is not None and not isinstance(
            last_error, TransientApiError
        ):
            raise PublishError(
                f"upload failed permanently for {asset.name}: {last_error}"
            ) from last_error
        if attempt < attempts:
            _retry_pause(attempt, retry_delay, sleep)

    detail = f": {last_error}" if last_error else ""
    raise PublishError(
        f"upload failed for {asset.name} after {attempts} attempts{detail}"
    )


def _publish_draft(
    backend: ReleaseBackend,
    release: ReleaseSnapshot,
    *,
    tag: str,
    prerelease: bool,
    notes: str,
    local_assets: tuple[LocalAsset, ...],
    attempts: int,
    retry_delay: float,
    sleep: Callable[[float], None],
) -> ReleaseSnapshot:
    for attempt in range(1, attempts + 1):
        print(f"release: publish attempt {attempt}/{attempts}")
        try:
            result = backend.publish(release.id)
            error = None
        except ApiError as caught:
            error = caught
            result = backend.refresh_release(release.id)

        _validate_metadata(
            result, tag=tag, prerelease=prerelease, notes=notes
        )
        _validate_remote_set(result, local_assets, require_complete=True)
        if not result.draft:
            return result
        if error is not None and not isinstance(error, TransientApiError):
            raise PublishError(f"could not publish release: {error}") from error
        if attempt < attempts:
            _retry_pause(attempt, retry_delay, sleep)
    raise PublishError(f"release remained a draft after {attempts} attempts")


def publish_release(
    backend: ReleaseBackend,
    *,
    tag: str,
    version: str,
    expected_commit: str,
    prerelease: bool,
    notes: str,
    assets: tuple[LocalAsset, ...],
    attempts: int,
    upload_timeout: float,
    retry_delay: float,
    sleep: Callable[[float], None] = time.sleep,
) -> ReleaseSnapshot:
    if attempts < 1:
        raise PublishError("attempts must be at least 1")
    if upload_timeout <= 0:
        raise PublishError("upload timeout must be positive")
    if retry_delay < 0:
        raise PublishError("retry delay cannot be negative")
    if tuple(asset.name for asset in assets) != expected_asset_names(version):
        raise PublishError("local assets are not in the deterministic expected order")

    tag_commit = backend.resolve_annotated_tag(tag)
    if tag_commit != expected_commit:
        raise PublishError(
            f"annotated tag resolves to {tag_commit}, not validated commit "
            f"{expected_commit}"
        )

    release = _create_or_resume(
        backend,
        tag=tag,
        prerelease=prerelease,
        notes=notes,
        attempts=attempts,
        retry_delay=retry_delay,
        sleep=sleep,
    )
    _validate_metadata(
        release, tag=tag, prerelease=prerelease, notes=notes
    )

    remote = _validate_remote_set(
        release, assets, require_complete=not release.draft
    )
    if not release.draft:
        print(f"release: already published and verified {tag}")
        return release

    for local in assets:
        existing = remote.get(local.name)
        if existing is not None and existing.state != "uploaded":
            release = _delete_incomplete(
                backend,
                release,
                existing,
                attempts=attempts,
                retry_delay=retry_delay,
                sleep=sleep,
            )
            remote = _validate_remote_set(
                release, assets, require_complete=False
            )
            existing = None
        if existing is not None:
            print(f"asset: already verified {local.name}")
            continue
        release = _upload_one(
            backend,
            release,
            local,
            assets,
            attempts=attempts,
            upload_timeout=upload_timeout,
            retry_delay=retry_delay,
            sleep=sleep,
        )
        remote = _validate_remote_set(
            release, assets, require_complete=False
        )

    _validate_remote_set(release, assets, require_complete=True)
    release = _publish_draft(
        backend,
        release,
        tag=tag,
        prerelease=prerelease,
        notes=notes,
        local_assets=assets,
        attempts=attempts,
        retry_delay=retry_delay,
        sleep=sleep,
    )
    print(f"release: published and verified {tag} with {len(assets)} assets")
    return release


class GitHubBackend:
    def __init__(
        self,
        repository: str,
        token: str,
        *,
        request_timeout: float,
        read_attempts: int = 3,
        retry_delay: float = 2,
        sleep: Callable[[float], None] = time.sleep,
    ):
        if not REPOSITORY_PATTERN.fullmatch(repository):
            raise PublishError("repository must be OWNER/REPO")
        if not token:
            raise PublishError("GITHUB_TOKEN is required")
        self.repository = repository
        self.token = token
        self.request_timeout = request_timeout
        self.read_attempts = read_attempts
        self.retry_delay = retry_delay
        self.sleep = sleep
        self.api_base = f"https://api.github.com/repos/{repository}"

    def _headers(self, content_type: str | None = None) -> dict[str, str]:
        headers = {
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {self.token}",
            "User-Agent": "dinorand-release-publisher",
            "X-GitHub-Api-Version": API_VERSION,
        }
        if content_type:
            headers["Content-Type"] = content_type
        return headers

    def _request_once(
        self,
        method: str,
        url: str,
        *,
        data=None,
        content_type: str | None = None,
        content_length: int | None = None,
        timeout: float | None = None,
    ):
        request = urllib.request.Request(
            url,
            data=data,
            headers=self._headers(content_type),
            method=method,
        )
        if content_length is not None:
            request.add_header("Content-Length", str(content_length))
        try:
            with urllib.request.urlopen(
                request, timeout=timeout or self.request_timeout
            ) as response:
                body = response.read()
        except urllib.error.HTTPError as error:
            try:
                detail = error.read(1000).decode("utf-8", errors="replace")
                message = json.loads(detail).get("message", detail)
            except (ValueError, AttributeError):
                message = str(error.reason)
            error_type = (
                TransientApiError
                if error.code in {408, 429} or error.code >= 500
                else ApiError
            )
            raise error_type(
                f"GitHub API {method} returned {error.code}: {message}",
                error.code,
            ) from error
        except (
            TimeoutError,
            socket.timeout,
            urllib.error.URLError,
            ConnectionError,
        ) as error:
            raise TransientApiError(
                f"GitHub API {method} network error: {error}"
            ) from error

        if not body:
            return None
        try:
            return json.loads(body)
        except (UnicodeError, json.JSONDecodeError) as error:
            raise ApiError(
                f"GitHub API {method} returned invalid JSON"
            ) from error

    def _read_json(self, url: str):
        for attempt in range(1, self.read_attempts + 1):
            try:
                return self._request_once("GET", url)
            except TransientApiError:
                if attempt == self.read_attempts:
                    raise
                _retry_pause(attempt, self.retry_delay, self.sleep)
        raise AssertionError("unreachable")

    @staticmethod
    def _snapshot(data) -> ReleaseSnapshot:
        return ReleaseSnapshot(
            id=int(data["id"]),
            tag_name=str(data["tag_name"]),
            name=str(data["name"] or ""),
            body=str(data["body"] or ""),
            draft=bool(data["draft"]),
            prerelease=bool(data["prerelease"]),
            upload_url=str(data["upload_url"]),
            assets=tuple(
                RemoteAsset(
                    id=int(asset["id"]),
                    name=str(asset["name"]),
                    size=int(asset["size"]),
                    digest=asset.get("digest"),
                    state=str(asset["state"]),
                )
                for asset in data.get("assets", [])
            ),
        )

    def resolve_annotated_tag(self, tag: str) -> str:
        encoded = urllib.parse.quote(tag, safe="")
        ref = self._read_json(f"{self.api_base}/git/ref/tags/{encoded}")
        target = ref.get("object", {})
        if target.get("type") != "tag":
            raise PublishError(f"{tag} must be an existing annotated tag")
        tag_object = self._read_json(
            f"{self.api_base}/git/tags/{target.get('sha', '')}"
        )
        commit = tag_object.get("object", {})
        if commit.get("type") != "commit" or not re.fullmatch(
            r"[0-9a-f]{40}", str(commit.get("sha", ""))
        ):
            raise PublishError(f"annotated tag {tag} does not target a commit")
        return str(commit["sha"])

    def find_release(self, tag: str) -> ReleaseSnapshot | None:
        matches = []
        for page in range(1, 101):
            releases = self._read_json(
                f"{self.api_base}/releases?per_page=100&page={page}"
            )
            matches.extend(
                release for release in releases if release.get("tag_name") == tag
            )
            if len(releases) < 100:
                break
        if len(matches) > 1:
            raise PublishError(f"multiple releases found for {tag}")
        return self._snapshot(matches[0]) if matches else None

    def create_draft(
        self, tag: str, name: str, body: str, prerelease: bool
    ) -> ReleaseSnapshot:
        payload = json.dumps(
            {
                "tag_name": tag,
                "name": name,
                "body": body,
                "draft": True,
                "prerelease": prerelease,
            }
        ).encode("utf-8")
        data = self._request_once(
            "POST",
            f"{self.api_base}/releases",
            data=payload,
            content_type="application/json",
            content_length=len(payload),
        )
        return self._snapshot(data)

    def refresh_release(self, release_id: int) -> ReleaseSnapshot:
        return self._snapshot(
            self._read_json(f"{self.api_base}/releases/{release_id}")
        )

    def upload_asset(
        self, release: ReleaseSnapshot, asset: LocalAsset, timeout: float
    ) -> None:
        base = release.upload_url.split("{", 1)[0]
        parts = urllib.parse.urlsplit(base)
        query = urllib.parse.parse_qsl(parts.query, keep_blank_values=True)
        query.append(("name", asset.name))
        url = urllib.parse.urlunsplit(
            (
                parts.scheme,
                parts.netloc,
                parts.path,
                urllib.parse.urlencode(query),
                parts.fragment,
            )
        )
        with asset.path.open("rb") as stream:
            self._request_once(
                "POST",
                url,
                data=stream,
                content_type=asset.content_type,
                content_length=asset.size,
                timeout=timeout,
            )

    def delete_asset(self, asset_id: int) -> None:
        self._request_once(
            "DELETE", f"{self.api_base}/releases/assets/{asset_id}"
        )

    def publish(self, release_id: int) -> ReleaseSnapshot:
        payload = b'{"draft":false}'
        data = self._request_once(
            "PATCH",
            f"{self.api_base}/releases/{release_id}",
            data=payload,
            content_type="application/json",
            content_length=len(payload),
        )
        return self._snapshot(data)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--expected-commit", required=True)
    parser.add_argument("--prerelease", choices=("true", "false"), required=True)
    parser.add_argument("--assets-dir", type=Path, required=True)
    parser.add_argument("--notes-file", type=Path, required=True)
    parser.add_argument("--attempts", type=int, default=3)
    parser.add_argument("--request-timeout", type=float, default=120)
    parser.add_argument("--upload-timeout", type=float, default=120)
    parser.add_argument("--retry-delay", type=float, default=2)
    args = parser.parse_args(argv)

    try:
        release_ref = parse_tag(args.tag)
        if args.version != release_ref.version:
            raise PublishError(
                f"version {args.version} does not match tag {args.tag}"
            )
        prerelease = args.prerelease == "true"
        if prerelease != release_ref.prerelease:
            raise PublishError(
                f"prerelease={prerelease} does not match tag {args.tag}"
            )
        if not re.fullmatch(r"[0-9a-f]{40}", args.expected_commit):
            raise PublishError("expected commit must be a full lowercase SHA")
        notes = args.notes_file.read_text(encoding="utf-8")
        if not notes.strip():
            raise PublishError("validated release notes are empty")
        assets = load_local_assets(args.assets_dir, args.version)
        token = os.environ.get("GITHUB_TOKEN", "")
        backend = GitHubBackend(
            args.repository,
            token,
            request_timeout=args.request_timeout,
            read_attempts=args.attempts,
            retry_delay=args.retry_delay,
        )
        publish_release(
            backend,
            tag=args.tag,
            version=args.version,
            expected_commit=args.expected_commit,
            prerelease=prerelease,
            notes=notes,
            assets=assets,
            attempts=args.attempts,
            upload_timeout=args.upload_timeout,
            retry_delay=args.retry_delay,
        )
    except (OSError, UnicodeError, ValidationError, PublishError) as error:
        print(f"release publication failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
