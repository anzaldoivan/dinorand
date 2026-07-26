#!/usr/bin/env python3
import hashlib
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

import release_publish


COMMIT = "a" * 40
TAG = "v1.2.3"
VERSION = "1.2.3"
NOTES = "Player-visible release notes.\n"


def make_local_assets(root: Path):
    contents = {
        f"dinorand-v{VERSION}-win-x64.zip": b"windows",
        f"dinorand-v{VERSION}-linux-x64.zip": b"linux",
        f"dinorand-v{VERSION}-osx-arm64.zip": b"macos",
        "dino_crisis_1.apworld": b"dc1",
        "dino_crisis_2.apworld": b"dc2",
    }
    for name, data in contents.items():
        (root / name).write_bytes(data)
    checksum_lines = [
        f"{hashlib.sha256(contents[name]).hexdigest()}  {name}"
        for name in sorted(contents)
    ]
    (root / "SHA256SUMS").write_text(
        "\n".join(checksum_lines) + "\n", encoding="ascii", newline="\n"
    )
    return release_publish.load_local_assets(root, VERSION)


def remote_asset(local, *, asset_id=1, state="uploaded", size=None, digest=None):
    return release_publish.RemoteAsset(
        id=asset_id,
        name=local.name,
        size=local.size if size is None else size,
        digest=f"sha256:{local.digest}" if digest is None else digest,
        state=state,
    )


def draft(assets=()):
    return release_publish.ReleaseSnapshot(
        id=42,
        tag_name=TAG,
        name=f"DinoRand {TAG}",
        body=NOTES,
        draft=True,
        prerelease=False,
        upload_url="https://uploads.example.test/releases/42/assets{?name,label}",
        assets=tuple(assets),
    )


class FakeBackend:
    def __init__(self, release=None):
        self.release = release
        self.created = 0
        self.uploaded = []
        self.deleted = []
        self.publish_calls = 0
        self.upload_outcomes = {}
        self.create_outcomes = []
        self.publish_outcomes = []
        self.tag_commit = COMMIT

    def resolve_annotated_tag(self, tag):
        self.resolved_tag = tag
        return self.tag_commit

    def find_release(self, tag):
        self.found_tag = tag
        return self.release

    def create_draft(self, tag, name, body, prerelease):
        self.created += 1
        if self.create_outcomes:
            outcome = self.create_outcomes.pop(0)
            if callable(outcome):
                outcome(self)
            elif isinstance(outcome, BaseException):
                raise outcome
        self.release = release_publish.ReleaseSnapshot(
            id=42,
            tag_name=tag,
            name=name,
            body=body,
            draft=True,
            prerelease=prerelease,
            upload_url="https://uploads.example.test/releases/42/assets{?name,label}",
            assets=(),
        )
        return self.release

    def refresh_release(self, release_id):
        self.refreshed_id = release_id
        return self.release

    def upload_asset(self, release, asset, timeout):
        self.uploaded.append((asset.name, timeout))
        outcomes = self.upload_outcomes.get(asset.name)
        if outcomes:
            outcome = outcomes.pop(0)
            if callable(outcome):
                outcome(self, asset)
            elif isinstance(outcome, BaseException):
                raise outcome
        else:
            self.add_asset(asset)

    def add_asset(self, asset):
        next_id = 1 + max((item.id for item in self.release.assets), default=0)
        self.release = replace(
            self.release,
            assets=self.release.assets + (remote_asset(asset, asset_id=next_id),),
        )

    def delete_asset(self, asset_id):
        self.deleted.append(asset_id)
        self.release = replace(
            self.release,
            assets=tuple(item for item in self.release.assets if item.id != asset_id),
        )

    def publish(self, release_id):
        self.publish_calls += 1
        if self.publish_outcomes:
            outcome = self.publish_outcomes.pop(0)
            if callable(outcome):
                outcome(self)
            elif isinstance(outcome, BaseException):
                raise outcome
        self.release = replace(self.release, draft=False)
        return self.release


class LocalAssetContractTests(unittest.TestCase):
    def test_exact_asset_set_and_checksums_are_required(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            assets = make_local_assets(root)
            self.assertEqual(
                [asset.name for asset in assets],
                list(release_publish.expected_asset_names(VERSION)),
            )

            (root / "unexpected.txt").write_text("no", encoding="utf-8")
            with self.assertRaisesRegex(release_publish.PublishError, "exactly"):
                release_publish.load_local_assets(root, VERSION)


class PublishContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.assets = make_local_assets(Path(self.temp.name))

    def tearDown(self):
        self.temp.cleanup()

    def publish(self, backend, **kwargs):
        return release_publish.publish_release(
            backend,
            tag=TAG,
            version=VERSION,
            expected_commit=COMMIT,
            prerelease=False,
            notes=NOTES,
            assets=self.assets,
            attempts=3,
            upload_timeout=17,
            retry_delay=0,
            sleep=lambda _: None,
            **kwargs,
        )

    def test_partial_draft_resumes_and_uploads_only_missing_assets_in_order(self):
        existing = tuple(remote_asset(asset, asset_id=i + 1) for i, asset in enumerate(self.assets[:3]))
        backend = FakeBackend(draft(existing))

        result = self.publish(backend)

        self.assertFalse(result.draft)
        self.assertEqual(backend.created, 0)
        self.assertEqual(
            backend.uploaded,
            [(asset.name, 17) for asset in self.assets[3:]],
        )
        self.assertEqual(backend.publish_calls, 1)

    def test_ambiguous_draft_creation_is_reconciled_before_retry(self):
        backend = FakeBackend()

        def create_then_timeout(fake):
            fake.release = draft()
            raise release_publish.TransientApiError("response timeout")

        backend.create_outcomes = [create_then_timeout]

        self.publish(backend)

        self.assertEqual(backend.created, 1)

    def test_ambiguous_transient_upload_is_reconciled_before_retry(self):
        backend = FakeBackend(draft())
        first = self.assets[0]

        def upload_then_timeout(fake, asset):
            fake.add_asset(asset)
            raise release_publish.TransientApiError("response timeout")

        backend.upload_outcomes[first.name] = [upload_then_timeout]

        self.publish(backend)

        self.assertEqual(
            [name for name, _ in backend.uploaded].count(first.name),
            1,
        )

    def test_transient_upload_failure_has_a_bounded_retry_count(self):
        backend = FakeBackend(draft())
        first = self.assets[0]
        backend.upload_outcomes[first.name] = [
            release_publish.TransientApiError("timeout"),
            release_publish.TransientApiError("timeout"),
            release_publish.TransientApiError("timeout"),
        ]

        with self.assertRaisesRegex(release_publish.PublishError, "3 attempts"):
            self.publish(backend)

        self.assertEqual(
            [name for name, _ in backend.uploaded],
            [first.name, first.name, first.name],
        )
        self.assertEqual(backend.publish_calls, 0)

    def test_incomplete_expected_asset_is_deleted_before_retry(self):
        first = self.assets[0]
        incomplete = remote_asset(
            first, asset_id=99, state="starter", size=0, digest=None
        )
        backend = FakeBackend(draft((incomplete,)))

        self.publish(backend)

        self.assertEqual(backend.deleted, [99])
        self.assertEqual(backend.uploaded[0], (first.name, 17))

    def test_unknown_incomplete_asset_state_fails_closed(self):
        first = self.assets[0]
        unknown = remote_asset(first, asset_id=99, state="processing")
        backend = FakeBackend(draft((unknown,)))

        with self.assertRaisesRegex(release_publish.PublishError, "processing"):
            self.publish(backend)

        self.assertEqual(backend.deleted, [])

    def test_mismatched_uploaded_asset_fails_closed(self):
        first = self.assets[0]
        mismatched = remote_asset(first, digest="sha256:" + "0" * 64)
        backend = FakeBackend(draft((mismatched,)))

        with self.assertRaisesRegex(release_publish.PublishError, "does not match"):
            self.publish(backend)

        self.assertEqual(backend.uploaded, [])
        self.assertEqual(backend.publish_calls, 0)

    def test_already_published_exact_release_is_idempotent(self):
        exact = tuple(remote_asset(asset, asset_id=i + 1) for i, asset in enumerate(self.assets))
        backend = FakeBackend(replace(draft(exact), draft=False))

        result = self.publish(backend)

        self.assertFalse(result.draft)
        self.assertEqual(backend.created, 0)
        self.assertEqual(backend.uploaded, [])
        self.assertEqual(backend.publish_calls, 0)

    def test_ambiguous_publish_is_reconciled_before_retry(self):
        exact = tuple(remote_asset(asset, asset_id=i + 1) for i, asset in enumerate(self.assets))
        backend = FakeBackend(draft(exact))

        def publish_then_timeout(fake):
            fake.release = replace(fake.release, draft=False)
            raise release_publish.TransientApiError("response timeout")

        backend.publish_outcomes = [publish_then_timeout]

        result = self.publish(backend)

        self.assertFalse(result.draft)
        self.assertEqual(backend.publish_calls, 1)

    def test_tag_must_be_annotated_and_resolve_to_validated_commit(self):
        backend = FakeBackend()
        backend.tag_commit = "b" * 40

        with self.assertRaisesRegex(release_publish.PublishError, "validated commit"):
            self.publish(backend)

        self.assertEqual(backend.created, 0)


if __name__ == "__main__":
    unittest.main()
