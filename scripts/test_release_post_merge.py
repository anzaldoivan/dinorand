#!/usr/bin/env python3
import unittest
from pathlib import Path
from unittest import mock

import release_post_merge
import release_tag


REPOSITORY = "anzaldoivan/dinorand"
MERGE_COMMIT = "a" * 40
TAG_OBJECT = "b" * 40
TAGGER_DATE = "2026-07-27T03:04:05Z"


def pull_request(
    *,
    action="closed",
    merged=True,
    base_ref="main",
    head_ref="version/v0.6.3",
    head_repo=REPOSITORY,
):
    return {
        "action": action,
        "pull_request": {
            "merged": merged,
            "base": {"ref": base_ref},
            "head": {"ref": head_ref, "repo": {"full_name": head_repo}},
        },
        "repository": {"full_name": REPOSITORY},
    }


class PullRequestEligibilityTests(unittest.TestCase):
    def test_valid_stable_and_prerelease_branches_map_to_tags(self):
        for branch, expected in (
            ("version/v0.6.3", "v0.6.3"),
            ("version/v2.0.0-rc.1", "v2.0.0-rc.1"),
        ):
            with self.subTest(branch=branch):
                decision = release_post_merge.classify_pull_request(
                    pull_request(head_ref=branch)
                )
                self.assertEqual(decision.tag, expected)

    def test_malformed_version_branches_fail(self):
        for branch in (
            "version/0.6.3",
            "version/v01.6.3",
            "version/v0.6",
            "version/v0.6.3+build.1",
            "version/v0.6.3-rc..1",
        ):
            with self.subTest(branch=branch):
                with self.assertRaises(release_post_merge.PostMergeValidationError):
                    release_post_merge.classify_pull_request(
                        pull_request(head_ref=branch)
                    )

    def test_ineligible_pull_requests_skip(self):
        cases = (
            pull_request(merged=False),
            pull_request(head_repo="someone/fork"),
            pull_request(head_ref="feature/release-scripts"),
            pull_request(base_ref="develop"),
            pull_request(action="opened"),
        )
        for event in cases:
            with self.subTest(event=event):
                self.assertIsNone(release_post_merge.classify_pull_request(event))

    @mock.patch("release_post_merge.run_git")
    def test_candidate_must_be_exact_merge_commit_on_main(self, run_git):
        run_git.side_effect = (
            mock.Mock(stdout=f"{MERGE_COMMIT}\n", returncode=0),
            mock.Mock(stdout="", stderr="", returncode=0),
        )

        release_post_merge.require_merge_commit(
            Path("/candidate"), MERGE_COMMIT
        )

        self.assertEqual(
            run_git.call_args_list,
            [
                mock.call(Path("/candidate"), "rev-parse", "--verify", "HEAD"),
                mock.call(
                    Path("/candidate"),
                    "merge-base",
                    "--is-ancestor",
                    MERGE_COMMIT,
                    "refs/remotes/origin/main",
                    check=False,
                ),
            ],
        )


class FakeApi:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []

    def request(self, method, path, payload=None):
        self.calls.append((method, path, payload))
        if not self.responses:
            raise AssertionError(f"unexpected API call: {method} {path}")
        response = self.responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return response


def ref_response(object_type="tag", sha=TAG_OBJECT):
    return {"object": {"type": object_type, "sha": sha}}


def tag_response(
    *,
    tag="v0.6.3",
    commit=MERGE_COMMIT,
    message="DinoRand v0.6.3",
    date=TAGGER_DATE,
):
    return {
        "sha": TAG_OBJECT,
        "tag": tag,
        "message": message,
        "object": {"type": "commit", "sha": commit},
        "tagger": {
            "name": release_tag.TAGGER_NAME,
            "email": release_tag.TAGGER_EMAIL,
            "date": date,
        },
    }


class ReleaseTaggerTests(unittest.TestCase):
    def spec(self):
        return release_tag.TagSpec.create(
            "v0.6.3", MERGE_COMMIT, TAGGER_DATE
        )

    def test_tag_targets_exact_merge_commit_through_annotated_tag_object(self):
        api = FakeApi(
            [
                release_tag.ApiError(404, "missing ref"),
                tag_response(),
                {},
                ref_response(),
                tag_response(),
            ]
        )

        release_tag.ensure_release_tag(
            api, REPOSITORY, self.spec(), token="configured-pat", sleep=lambda _: None
        )

        create_tag = api.calls[1]
        create_ref = api.calls[2]
        self.assertEqual(create_tag[0:2], ("POST", f"/repos/{REPOSITORY}/git/tags"))
        self.assertEqual(create_tag[2]["object"], MERGE_COMMIT)
        self.assertEqual(create_tag[2]["type"], "commit")
        self.assertEqual(create_ref[2], {"ref": "refs/tags/v0.6.3", "sha": TAG_OBJECT})

    def test_existing_exact_annotated_tag_is_idempotent(self):
        api = FakeApi([ref_response(), tag_response()])

        release_tag.ensure_release_tag(
            api, REPOSITORY, self.spec(), token="configured-pat"
        )

        self.assertTrue(all(method == "GET" for method, _, _ in api.calls))

    def test_lightweight_or_mismatched_tags_fail_closed(self):
        cases = (
            [ref_response(object_type="commit", sha=MERGE_COMMIT)],
            [ref_response(), tag_response(commit="c" * 40)],
            [ref_response(), tag_response(message="unexpected")],
        )
        for responses in cases:
            with self.subTest(responses=responses):
                api = FakeApi(responses)
                with self.assertRaises(release_tag.TaggingError):
                    release_tag.ensure_release_tag(
                        api, REPOSITORY, self.spec(), token="configured-pat"
                    )
                self.assertTrue(all(method == "GET" for method, _, _ in api.calls))

    def test_ambiguous_ref_response_reconciles_before_retry(self):
        api = FakeApi(
            [
                release_tag.ApiError(404, "missing ref"),
                tag_response(),
                release_tag.TransientApiError("timed out"),
                ref_response(),
                tag_response(),
            ]
        )

        release_tag.ensure_release_tag(
            api,
            REPOSITORY,
            self.spec(),
            token="configured-pat",
            attempts=3,
            sleep=lambda _: None,
        )

        self.assertEqual(
            [method for method, _, _ in api.calls],
            ["GET", "POST", "POST", "GET", "GET"],
        )

    def test_missing_pat_fails_before_any_api_request(self):
        api = FakeApi([])

        with self.assertRaisesRegex(release_tag.TaggingError, "RELEASE_TAG_TOKEN"):
            release_tag.ensure_release_tag(api, REPOSITORY, self.spec(), token="")

        self.assertEqual(api.calls, [])


class WorkflowContractTests(unittest.TestCase):
    def test_post_merge_workflow_uses_trusted_control_and_candidate_as_data(self):
        workflow = (
            Path(__file__).parents[1]
            / ".github"
            / "workflows"
            / "post-merge-release-tag.yml"
        ).read_text(encoding="utf-8")

        self.assertIn("pull_request_target:", workflow)
        self.assertIn("types: [closed]", workflow)
        self.assertIn("branches: [main]", workflow)
        self.assertIn("github.event.pull_request.merged == true", workflow)
        self.assertIn(
            "github.event.pull_request.head.repo.full_name == github.repository",
            workflow,
        )
        self.assertIn("startsWith(github.event.pull_request.head.ref, 'version/')", workflow)
        self.assertIn("ref: ${{ github.event.pull_request.base.sha }}", workflow)
        self.assertIn("path: control", workflow)
        self.assertIn("ref: ${{ github.event.pull_request.merge_commit_sha }}", workflow)
        self.assertIn("path: candidate", workflow)
        self.assertIn("python3 control/scripts/release_post_merge.py", workflow)
        self.assertIn("python3 control/scripts/release_tag.py", workflow)
        self.assertIn("RELEASE_TAG_TOKEN: ${{ secrets.RELEASE_TAG_TOKEN }}", workflow)
        self.assertNotIn("run: candidate/", workflow)


if __name__ == "__main__":
    unittest.main()
