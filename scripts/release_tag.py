#!/usr/bin/env python3
"""Create or reconcile one exact annotated release tag through GitHub's API."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import os
import re
import sys
import time
from urllib import error, parse, request

from release_validate import ValidationError, parse_tag


TAGGER_NAME = "DinoRand Release Automation"
TAGGER_EMAIL = "actions@github.com"
FULL_SHA = re.compile(r"^[0-9a-f]{40}$")
REPOSITORY_NAME = re.compile(
    r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"
)


class TaggingError(RuntimeError):
    pass


class ApiError(TaggingError):
    def __init__(self, status: int, message: str):
        super().__init__(f"GitHub API returned {status}: {message}")
        self.status = status


class TransientApiError(TaggingError):
    pass


def normalize_timestamp(value: str) -> str:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise TaggingError(f"invalid tagger timestamp: {value!r}") from error
    if parsed.tzinfo is None:
        raise TaggingError("tagger timestamp must include a timezone")
    return (
        parsed.astimezone(timezone.utc)
        .replace(microsecond=0)
        .strftime("%Y-%m-%dT%H:%M:%SZ")
    )


@dataclass(frozen=True)
class TagSpec:
    tag: str
    commit: str
    message: str
    tagger_date: str

    @classmethod
    def create(cls, tag: str, commit: str, tagger_date: str) -> "TagSpec":
        try:
            parse_tag(tag)
        except ValidationError as error:
            raise TaggingError(str(error)) from error
        if not FULL_SHA.fullmatch(commit):
            raise TaggingError("tag target must be a full lowercase commit SHA")
        return cls(
            tag=tag,
            commit=commit,
            message=f"DinoRand {tag}",
            tagger_date=normalize_timestamp(tagger_date),
        )

    def tag_payload(self) -> dict:
        return {
            "tag": self.tag,
            "message": self.message,
            "object": self.commit,
            "type": "commit",
            "tagger": {
                "name": TAGGER_NAME,
                "email": TAGGER_EMAIL,
                "date": self.tagger_date,
            },
        }


class GitHubApi:
    def __init__(
        self,
        token: str,
        *,
        api_url: str = "https://api.github.com",
        timeout: int = 30,
    ):
        self.token = token
        self.api_url = api_url.rstrip("/")
        self.timeout = timeout

    def request(self, method: str, path: str, payload=None):
        data = None
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        api_request = request.Request(
            f"{self.api_url}{path}",
            data=data,
            method=method,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {self.token}",
                "Content-Type": "application/json",
                "User-Agent": "dinorand-release-tagger",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )
        try:
            with request.urlopen(api_request, timeout=self.timeout) as response:
                body = response.read()
        except error.HTTPError as api_error:
            body = api_error.read().decode("utf-8", errors="replace")
            message = body
            try:
                message = json.loads(body).get("message", body)
            except json.JSONDecodeError:
                pass
            if (
                api_error.code in (408, 409, 429)
                or 500 <= api_error.code <= 599
            ):
                raise TransientApiError(
                    f"GitHub API returned {api_error.code}: {message}"
                ) from api_error
            raise ApiError(api_error.code, message) from api_error
        except (error.URLError, TimeoutError) as api_error:
            raise TransientApiError(
                f"GitHub API request failed: {api_error}"
            ) from api_error

        if not body:
            return {}
        try:
            decoded = json.loads(body)
        except json.JSONDecodeError as decode_error:
            raise TransientApiError(
                "GitHub API returned malformed JSON"
            ) from decode_error
        if not isinstance(decoded, dict):
            raise TransientApiError("GitHub API returned an unexpected response")
        return decoded


def _paths(repository: str, tag: str) -> tuple[str, str]:
    if not REPOSITORY_NAME.fullmatch(repository):
        raise TaggingError(f"invalid GitHub repository name: {repository!r}")
    encoded_tag = parse.quote(tag, safe="")
    base = f"/repos/{repository}/git"
    return f"{base}/ref/tags/{encoded_tag}", f"{base}/tags"


def _validate_tag_object(tag_object: dict, spec: TagSpec) -> None:
    expected = {
        "tag": spec.tag,
        "message": spec.message,
        "object_type": "commit",
        "object_sha": spec.commit,
        "tagger_name": TAGGER_NAME,
        "tagger_email": TAGGER_EMAIL,
        "tagger_date": spec.tagger_date,
    }
    try:
        actual = {
            "tag": tag_object["tag"],
            "message": tag_object["message"],
            "object_type": tag_object["object"]["type"],
            "object_sha": tag_object["object"]["sha"],
            "tagger_name": tag_object["tagger"]["name"],
            "tagger_email": tag_object["tagger"]["email"],
            "tagger_date": normalize_timestamp(
                tag_object["tagger"]["date"]
            ),
        }
    except (KeyError, TypeError, TaggingError) as error:
        raise TaggingError(
            "existing annotated tag has incomplete or invalid metadata"
        ) from error
    if actual != expected:
        differences = [
            key
            for key in expected
            if actual.get(key) != expected[key]
        ]
        raise TaggingError(
            "existing annotated tag does not match required "
            f"metadata: {', '.join(differences)}"
        )


def reconcile_existing(api, repository: str, spec: TagSpec) -> bool:
    ref_path, tags_path = _paths(repository, spec.tag)
    try:
        reference = api.request("GET", ref_path)
    except ApiError as api_error:
        if api_error.status == 404:
            return False
        raise
    try:
        object_type = reference["object"]["type"]
        object_sha = reference["object"]["sha"]
    except (KeyError, TypeError) as error:
        raise TaggingError("tag reference response is incomplete") from error
    if object_type != "tag":
        raise TaggingError(
            f"refs/tags/{spec.tag} is lightweight or points to {object_type!r}"
        )
    if not FULL_SHA.fullmatch(object_sha):
        raise TaggingError("annotated tag reference has an invalid object SHA")
    tag_object = api.request("GET", f"{tags_path}/{object_sha}")
    if tag_object.get("sha") != object_sha:
        raise TaggingError(
            "annotated tag object response does not match its reference"
        )
    _validate_tag_object(tag_object, spec)
    return True


def ensure_release_tag(
    api,
    repository: str,
    spec: TagSpec,
    *,
    token: str,
    attempts: int = 3,
    retry_delay: float = 2,
    sleep=time.sleep,
) -> None:
    if not token.strip():
        raise TaggingError(
            "RELEASE_TAG_TOKEN is required before tag creation"
        )
    if attempts < 1:
        raise TaggingError("attempts must be at least 1")
    ref_path, tags_path = _paths(repository, spec.tag)
    last_error: Exception | None = None

    for attempt in range(1, attempts + 1):
        try:
            if reconcile_existing(api, repository, spec):
                return
        except TransientApiError as transient:
            last_error = transient
            if attempt == attempts:
                break
            sleep(retry_delay)
            continue

        try:
            tag_object = api.request(
                "POST", tags_path, spec.tag_payload()
            )
            _validate_tag_object(tag_object, spec)
            object_sha = tag_object.get("sha")
            if not isinstance(object_sha, str) or not FULL_SHA.fullmatch(
                object_sha
            ):
                raise TaggingError(
                    "created annotated tag returned an invalid object SHA"
                )
            api.request(
                "POST",
                f"/repos/{repository}/git/refs",
                {"ref": f"refs/tags/{spec.tag}", "sha": object_sha},
            )
        except (ApiError, TransientApiError) as mutation_error:
            last_error = mutation_error
            try:
                if reconcile_existing(api, repository, spec):
                    return
            except TransientApiError as reconcile_error:
                last_error = reconcile_error
            if not isinstance(mutation_error, TransientApiError):
                raise TaggingError(
                    f"tag creation failed and no exact tag exists: {mutation_error}"
                ) from mutation_error
        else:
            try:
                if reconcile_existing(api, repository, spec):
                    return
            except TransientApiError as reconcile_error:
                last_error = reconcile_error

        if attempt < attempts:
            sleep(retry_delay)

    raise TaggingError(
        f"could not create and reconcile exact tag after {attempts} attempts: "
        f"{last_error or 'tag reference remained absent'}"
    )


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--tagger-date", required=True)
    parser.add_argument("--token-env", default="RELEASE_TAG_TOKEN")
    parser.add_argument("--api-url", default="https://api.github.com")
    parser.add_argument("--attempts", type=int, default=3)
    parser.add_argument("--request-timeout", type=int, default=30)
    parser.add_argument("--retry-delay", type=float, default=2)
    args = parser.parse_args(argv)

    try:
        token = os.environ.get(args.token_env, "")
        if not token.strip():
            raise TaggingError(
                f"{args.token_env} is required before tag creation"
            )
        spec = TagSpec.create(args.tag, args.commit, args.tagger_date)
        api = GitHubApi(
            token, api_url=args.api_url, timeout=args.request_timeout
        )
        ensure_release_tag(
            api,
            args.repository,
            spec,
            token=token,
            attempts=args.attempts,
            retry_delay=args.retry_delay,
        )
    except TaggingError as tagging_error:
        print(f"release tagging failed: {tagging_error}", file=sys.stderr)
        return 1

    print(
        f"verified annotated tag {spec.tag} at exact merge commit {spec.commit}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
