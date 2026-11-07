import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


path = Path(__file__).resolve().parents[1] / "scripts/release_policy.py"
spec = importlib.util.spec_from_file_location("release_policy", path)
policy = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = policy
spec.loader.exec_module(policy)


class ReleasePolicyTests(unittest.TestCase):
    commit = "a" * 40
    other = "b" * 40

    def refs(self, branch, tag=None):
        refs = {"refs/heads/" + branch: self.commit}
        if tag:
            refs["refs/tags/" + tag] = self.commit
        return refs

    def test_each_channel_uses_its_own_branch_for_tags_and_manual_runs(self):
        for tag, channel, branch in [("v1.2.3.0", "stable", "master"), ("v1.2.3.4-beta.1", "beta", "beta"), ("v1.2.3.5-test.2", "test", "test")]:
            for event in ["push", "workflow_dispatch"]:
                with self.subTest(tag=tag, event=event):
                    ref = "refs/tags/" + tag if event == "push" else "refs/heads/" + branch
                    plan = policy.make_plan(event, ref, self.commit, self.commit, tag, self.refs(branch, tag if event == "push" else None))
                    self.assertEqual((plan.channel, plan.branch, plan.version), (channel, branch, tag[1:].split("-")[0]))
                    self.assertEqual(plan.source_commit, self.commit)
                    self.assertEqual(plan.tag_exists, event == "push")

    def test_other_branches_cannot_create_stable_or_prerelease_builds(self):
        for selected in ["dev", "development", "feature/proxy", "master", "beta", "test"]:
            for tag, expected in [("v1.2.3.0", "master"), ("v1.2.3.4-beta.1", "beta"), ("v1.2.3.4-test.1", "test")]:
                if selected == expected:
                    continue
                with self.subTest(selected=selected, tag=tag), self.assertRaises(policy.Refusal):
                    policy.make_plan("workflow_dispatch", "refs/heads/" + selected, self.commit, self.commit, tag, self.refs(expected))

    def test_ambiguous_versions_and_untrusted_input_are_rejected(self):
        for tag in ["", "v1.2.3", "1.2.3.0", "v1.2.3.4", "v1.2.3.0-rc.1", "v1.2.3.0-beta.0", "v01.2.3.0", "v1.2.3.0-test.01", "v65536.2.3.0", "v1.2.3.0\nchannel=stable", "v1.2.3.0;exit", "v1.2.3.0/extra"]:
            with self.subTest(tag=tag), self.assertRaises(policy.Refusal):
                policy.parse_tag(tag)

    def test_existing_suffixes_without_a_counter_remain_accepted(self):
        self.assertEqual(policy.parse_tag("v1.2.3.4-beta"), ("1.2.3.4", "beta"))
        self.assertEqual(policy.parse_tag("v1.2.3.4-test"), ("1.2.3.4", "test"))

    def test_a_numeric_version_is_not_reused_by_another_channel_or_build(self):
        for other in ["v1.2.3.4-beta.1", "v1.2.3.4-test.1"]:
            refs = self.refs("test", other)
            with self.subTest(other=other), self.assertRaises(policy.Refusal):
                policy.make_plan("workflow_dispatch", "refs/heads/test", self.commit, self.commit, "v1.2.3.4-test.2", refs)

    def test_version_prefixes_and_existing_release_retries_remain_distinct(self):
        tag = "v1.2.3.4-test.2"
        refs = self.refs("test", tag)
        refs["refs/tags/v1.2.3.40-beta.1"] = self.other
        refs["refs/tags/v1.2.3.4-not-a-release"] = self.other
        self.assertEqual(policy.make_plan("push", "refs/tags/" + tag, self.commit, self.commit, "", refs).release_tag, tag)

    def test_a_tag_cannot_release_development_only_commits(self):
        refs = self.refs("master", "v1.2.3.0")
        refs["refs/heads/master"] = self.other
        with self.assertRaises(policy.Refusal):
            policy.make_plan("push", "refs/tags/v1.2.3.0", self.commit, self.commit, "", refs)

    def test_changed_checkout_or_branch_head_stops_before_building(self):
        for event_commit, checkout, branch in [(self.other, self.commit, self.commit), (self.commit, self.other, self.commit), (self.commit, self.commit, self.other)]:
            refs = {"refs/heads/master": branch}
            with self.subTest(event=event_commit, checkout=checkout, branch=branch), self.assertRaises(policy.Refusal):
                policy.make_plan("workflow_dispatch", "refs/heads/master", event_commit, checkout, "v1.2.3.0", refs)

    def test_tag_push_requires_a_live_tag_and_manual_tag_refs_are_rejected(self):
        with self.assertRaises(policy.Refusal):
            policy.make_plan("push", "refs/tags/v1.2.3.0", self.commit, self.commit, "", self.refs("master"))
        with self.assertRaises(policy.Refusal):
            policy.make_plan("workflow_dispatch", "refs/tags/v1.2.3.0", self.commit, self.commit, "v1.2.3.0", self.refs("master"))

    def test_pushes_and_other_events_do_not_publish_branches(self):
        for event in ["push", "pull_request", "schedule", "workflow_run"]:
            with self.subTest(event=event), self.assertRaises(policy.Refusal):
                policy.make_plan(event, "refs/heads/master", self.commit, self.commit, "v1.2.3.0", self.refs("master"))

    def test_annotated_tag_is_compared_by_its_peeled_commit(self):
        refs = self.refs("master", "v1.2.3.0")
        refs["refs/tags/v1.2.3.0"] = self.other
        refs["refs/tags/v1.2.3.0^{}"] = self.commit
        plan = policy.make_plan("push", "refs/tags/v1.2.3.0", self.commit, self.commit, "", refs)
        self.assertTrue(plan.tag_exists)

    def test_publication_rechecks_tag_creation_deletion_and_retargeting(self):
        self.assertFalse(policy.validate_tag({}, "v1.2.3.0", self.commit, False))
        self.assertTrue(policy.validate_tag(self.refs("master", "v1.2.3.0"), "v1.2.3.0", self.commit, True))
        with self.assertRaises(policy.Refusal):
            policy.validate_tag({}, "v1.2.3.0", self.commit, True)
        for required in [True, False]:
            with self.subTest(required=required), self.assertRaises(policy.Refusal):
                policy.validate_tag({"refs/tags/v1.2.3.0": self.other}, "v1.2.3.0", self.commit, required)

    def test_remote_response_does_not_accept_conflicting_or_invalid_references(self):
        for response in ["not-a-reference", self.commit + " refs/heads/master extra", "x" * 40 + " refs/heads/master", self.commit + " refs/heads/master\n" + self.other + " refs/heads/master"]:
            with self.subTest(response=response), self.assertRaises(policy.Refusal):
                policy.read_refs(response)
        self.assertEqual(policy.read_refs(self.commit + "\trefs/heads/master\n"), self.refs("master"))

    def test_outputs_share_one_version_channel_and_immutable_commit(self):
        plan = policy.make_plan("workflow_dispatch", "refs/heads/test", self.commit, self.commit, "v1.2.3.4-test.2", self.refs("test"))
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "output"
            policy.emit(plan, target)
            values = dict(line.split("=", 1) for line in target.read_text().splitlines())
            self.assertEqual(values["version"], "1.2.3.4")
            self.assertEqual(values["channel"], "test")
            self.assertEqual(values["source_commit"], self.commit)
            self.assertEqual(values["tag_exists"], "false")


if __name__ == "__main__":
    unittest.main()
