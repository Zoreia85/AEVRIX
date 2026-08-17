import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from tools.mobile_lab.integration_http import HttpResponse, IntegrationPolicyError
from tools.mobile_lab.integration_tools import MobSFLocalEndpoint
from tools.mobile_lab.mobsf_local import MobSFLocalApiClient, MobSFResponse
from tools.mobile_lab.tool_invocation import AuthorizedArtifact
from tools.mobile_lab.tool_provenance import GitHubProvenanceApiClient, summarize_release


class FakeGitHubTransport:
    def __init__(self, body=b'{}'):
        self.body = body
        self.requests = []
    def send(self, request):
        self.requests.append(request)
        return HttpResponse(200, request.url, {}, self.body)


class GitHubProvenanceApiTests(unittest.TestCase):
    def test_latest_release_and_asset_paths_are_read_only(self):
        tx = FakeGitHubTransport()
        client = GitHubProvenanceApiClient(tx)
        client.latest_release("skylot", "jadx")
        client.release_asset("skylot", "jadx", 42)
        self.assertTrue(tx.requests[0].url.endswith("/repos/skylot/jadx/releases/latest"))
        self.assertTrue(tx.requests[1].url.endswith("/repos/skylot/jadx/releases/assets/42"))
        self.assertTrue(all(r.method == "GET" for r in tx.requests))

    def test_commit_ref_is_encoded(self):
        tx = FakeGitHubTransport()
        GitHubProvenanceApiClient(tx).commit("skylot", "jadx", "release/v1")
        self.assertIn("release%2Fv1", tx.requests[0].url)

    def test_user_attestation_digest_and_predicate(self):
        tx = FakeGitHubTransport(body=b'{"attestations":[]}')
        GitHubProvenanceApiClient(tx).user_attestations("octocat", "a" * 64, "provenance")
        url = tx.requests[0].url
        self.assertIn("/users/octocat/attestations/sha256:" + "a" * 64, url)
        self.assertIn("predicate_type=provenance", url)

    def test_organization_attestation_rejects_invalid_digest(self):
        with self.assertRaises(ValueError):
            GitHubProvenanceApiClient(FakeGitHubTransport()).organization_attestations("MobSF", "bad")


class ProvenanceSummaryTests(unittest.TestCase):
    def test_release_asset_digest_commit_signature_and_attestation_are_distinct(self):
        release = {
            "tag_name": "v1.2.3", "published_at": "2026-01-01T00:00:00Z",
            "draft": False, "prerelease": False,
            "assets": [
                {"id": 1, "name": "tool.zip", "size": 100, "digest": "sha256:" + "a" * 64},
                {"id": 2, "name": "legacy.zip", "size": 50},
            ],
        }
        commit = {"sha": "b" * 40, "commit": {"verification": {"verified": True, "reason": "valid"}}}
        snapshot = summarize_release("owner/repo", release, commit, {"attestations": [{"repository_id": 1}]})
        self.assertEqual("SHA256_PRESENT", snapshot.assets[0].digest_status)
        self.assertEqual("UNMEASURED", snapshot.assets[1].digest_status)
        self.assertTrue(snapshot.commit_signature_verified)
        self.assertEqual("PRESENT_UNVERIFIED", snapshot.attestation_status)
        self.assertEqual(1, snapshot.attestation_count)

    def test_attestation_presence_is_not_cryptographic_verification(self):
        snapshot = summarize_release("owner/repo", {"assets": []}, attestations={"attestations": [{}]})
        self.assertNotEqual("VERIFIED", snapshot.attestation_status)


class FakeMobSFTransport:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []
    def post_file(self, path, field_name, file_path, api_key):
        self.calls.append(("file", path, field_name, Path(file_path).name, bool(api_key)))
        return self.responses.pop(0)
    def post_form(self, path, fields, api_key):
        self.calls.append(("form", path, dict(fields), bool(api_key)))
        return self.responses.pop(0)


class MobSFLocalClientTests(unittest.TestCase):
    def _artifact(self, root, content=b"apk-bytes"):
        path = Path(root) / "sample.apk"
        path.write_bytes(content)
        return AuthorizedArtifact(str(path), hashlib.sha256(content).hexdigest(), True, "apk")

    def test_server_isolation_must_be_explicit(self):
        with self.assertRaises(IntegrationPolicyError):
            MobSFLocalApiClient(MobSFLocalEndpoint("http://127.0.0.1:8000"), "key", False, FakeMobSFTransport([]))

    def test_api_key_is_required(self):
        with self.assertRaises(ValueError):
            MobSFLocalApiClient(MobSFLocalEndpoint("http://127.0.0.1:8000"), "", True, FakeMobSFTransport([]))

    def test_hash_mismatch_blocks_before_upload(self):
        with tempfile.TemporaryDirectory() as root:
            artifact = self._artifact(root)
            bad = AuthorizedArtifact(artifact.path, "0" * 64, True, "apk")
            tx = FakeMobSFTransport([])
            client = MobSFLocalApiClient(MobSFLocalEndpoint("http://127.0.0.1:8000"), "key", True, tx)
            with self.assertRaises(IntegrationPolicyError):
                client.upload(bad)
            self.assertEqual([], tx.calls)

    def test_full_static_analysis_is_local_and_cleanup_is_evidenced(self):
        with tempfile.TemporaryDirectory() as root:
            artifact = self._artifact(root)
            responses = [
                MobSFResponse(200, {}, json.dumps({"hash": "a" * 32, "scan_type": "apk", "file_name": "sample.apk"}).encode()),
                MobSFResponse(200, {}, b'{"scan":"ok"}'),
                MobSFResponse(200, {}, b'{"permissions":[],"security_score":80}'),
                MobSFResponse(200, {}, b'{"deleted":"yes"}'),
            ]
            tx = FakeMobSFTransport(responses)
            client = MobSFLocalApiClient(MobSFLocalEndpoint("http://127.0.0.1:8000"), "key", True, tx)
            result = client.analyze(artifact, cleanup=True)
            self.assertTrue(result.cleanup_completed)
            self.assertEqual(4, len(result.evidence))
            self.assertEqual(["upload", "scan", "report-json", "delete-scan"], [e.operation for e in result.evidence])
            self.assertEqual(["/api/v1/upload", "/api/v1/scan", "/api/v1/report_json", "/api/v1/delete_scan"], [c[1] for c in tx.calls])
            self.assertEqual(80, result.report["security_score"])

    def test_report_must_be_json_object(self):
        with tempfile.TemporaryDirectory() as root:
            artifact = self._artifact(root)
            tx = FakeMobSFTransport([
                MobSFResponse(200, {}, json.dumps({"hash": "a" * 32, "scan_type": "apk", "file_name": "sample.apk"}).encode()),
                MobSFResponse(200, {}, b'{"scan":"ok"}'),
                MobSFResponse(200, {}, b'[]'),
                MobSFResponse(200, {}, b'{"deleted":"yes"}'),
            ])
            client = MobSFLocalApiClient(MobSFLocalEndpoint("http://127.0.0.1:8000"), "key", True, tx)
            with self.assertRaises(IntegrationPolicyError):
                client.analyze(artifact)
            self.assertEqual("/api/v1/delete_scan", tx.calls[-1][1])


if __name__ == "__main__":
    unittest.main()
