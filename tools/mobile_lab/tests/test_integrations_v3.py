import json
import tempfile
import unittest
from pathlib import Path
from subprocess import CompletedProcess

from tools.mobile_lab import integration_fabric as m


class FakeTransport:
    def __init__(self, status=200, body=b'{}', headers=None, url=None):
        self.status = status
        self.body = body
        self.headers = headers or {}
        self.url = url
        self.requests = []

    def send(self, request):
        self.requests.append(request)
        return m.HttpResponse(self.status, self.url or request.url, self.headers, self.body)


class GitHubApiTests(unittest.TestCase):
    def test_headers_include_pinned_api_version_and_token(self):
        transport = FakeTransport(headers={"X-RateLimit-Limit": "5000", "X-RateLimit-Remaining": "4999"})
        client = m.GitHubPublicApiClient(transport, token="secret")
        result = client.repository("skylot", "jadx")
        request = transport.requests[0]
        self.assertEqual(m.GITHUB_API_VERSION, request.headers["X-GitHub-Api-Version"])
        self.assertEqual("Bearer secret", request.headers["Authorization"])
        self.assertEqual(5000, result.evidence.rate_limit.limit)
        self.assertEqual(4999, result.evidence.rate_limit.remaining)
        self.assertNotIn("secret", result.evidence.request_sha256)

    def test_etag_conditional_request_and_304(self):
        transport = FakeTransport(status=304, body=b"", headers={"ETag": '"abc"'})
        result = m.GitHubPublicApiClient(transport).releases("frida", "frida", etag='"abc"')
        self.assertEqual('"abc"', transport.requests[0].headers["If-None-Match"])
        self.assertTrue(result.not_modified)
        self.assertIsNone(result.data)

    def test_workflow_artifacts_path_is_read_only(self):
        transport = FakeTransport(body=b'{"total_count":0,"artifacts":[]}')
        m.GitHubPublicApiClient(transport).workflow_artifacts("Zoreia85", "AEVRIX", 123)
        request = transport.requests[0]
        self.assertEqual("GET", request.method)
        self.assertIn("/actions/runs/123/artifacts", request.url)

    def test_dependency_sbom_path(self):
        transport = FakeTransport(body=b'{"sbom":{"spdxVersion":"SPDX-2.3"}}')
        result = m.GitHubPublicApiClient(transport).dependency_sbom("Zoreia85", "AEVRIX")
        self.assertIn("/dependency-graph/sbom", transport.requests[0].url)
        self.assertEqual("SPDX-2.3", result.data["sbom"]["spdxVersion"])

    def test_global_advisories_query_is_encoded(self):
        transport = FakeTransport(body=b'[]')
        m.GitHubPublicApiClient(transport).global_advisories("pip", "requests")
        url = transport.requests[0].url
        self.assertIn("ecosystem=pip", url)
        self.assertIn("affects=requests", url)

    def test_rejects_repository_path_injection(self):
        with self.assertRaises(ValueError):
            m.GitHubPublicApiClient(FakeTransport()).repository("good", "bad/../../repo")

    def test_response_hash_is_stable(self):
        body = b'{"name":"jadx"}'
        result = m.GitHubPublicApiClient(FakeTransport(body=body)).repository("skylot", "jadx")
        import hashlib
        self.assertEqual(hashlib.sha256(body).hexdigest(), result.evidence.response_sha256)


class OSVTests(unittest.TestCase):
    def test_package_query(self):
        transport = FakeTransport(body=b'{"vulns":[]}')
        result = m.OSVApiClient(transport).query_package("requests", "PyPI", "2.31.0")
        request = transport.requests[0]
        payload = json.loads(request.body)
        self.assertEqual("requests", payload["package"]["name"])
        self.assertEqual("PyPI", payload["package"]["ecosystem"])
        self.assertEqual("2.31.0", payload["version"])
        self.assertEqual("osv-api", result.evidence.source_id)

    def test_commit_query_rejects_non_hex(self):
        with self.assertRaises(ValueError):
            m.OSVApiClient(FakeTransport()).query_commit("not-a-sha")

    def test_batch_is_bounded(self):
        with self.assertRaises(ValueError):
            m.OSVApiClient(FakeTransport()).query_batch([])


class BoundaryTests(unittest.TestCase):
    def test_mobsf_is_loopback_only(self):
        m.MobSFLocalEndpoint("http://127.0.0.1:8000").validate()
        m.MobSFLocalEndpoint("http://localhost:8000").validate()
        with self.assertRaises(m.IntegrationPolicyError):
            m.MobSFLocalEndpoint("https://mobsf.example.com").validate()

    def test_mobsf_url_cannot_embed_credentials(self):
        with self.assertRaises(m.IntegrationPolicyError):
            m.MobSFLocalEndpoint("http://user:pass@127.0.0.1:8000").validate()

    def test_instrumentation_policy_allows_observation(self):
        m.InstrumentationObservationPolicy.validate("trace_calls", "send('called')")

    def test_instrumentation_policy_rejects_mutation(self):
        with self.assertRaises(m.IntegrationPolicyError):
            m.InstrumentationObservationPolicy.validate("write_memory", "")

    def test_instrumentation_policy_rejects_bypass_script(self):
        with self.assertRaises(m.IntegrationPolicyError):
            m.InstrumentationObservationPolicy.validate("trace_calls", "// bypass certificate pinning")

    def test_https_transport_rejects_http_and_untrusted_host(self):
        transport = m.BoundedHttpsTransport(["api.github.com"])
        with self.assertRaises(m.IntegrationPolicyError):
            transport.send(m.HttpRequest("GET", "http://api.github.com/repos/a/b"))
        with self.assertRaises(m.IntegrationPolicyError):
            transport.send(m.HttpRequest("GET", "https://evil.example/repos/a/b"))


class ToolProbeTests(unittest.TestCase):
    def test_not_found_is_evidence_not_exception(self):
        probe = m.LocalToolProbe(which=lambda _: None)
        result = probe.probe(m.ToolProbeSpec("jadx", ("jadx",), ("--version",)))
        self.assertEqual("NOT_FOUND", result.status)
        self.assertIsNone(result.executable_sha256)

    def test_executable_is_hashed_and_versioned(self):
        with tempfile.TemporaryDirectory() as td:
            exe = Path(td) / "tool"
            exe.write_bytes(b"binary-v1")
            def runner(args, **kwargs):
                self.assertFalse(kwargs["shell"])
                return CompletedProcess(args, 0, stdout="tool 1.2.3\n")
            probe = m.LocalToolProbe(which=lambda _: str(exe), runner=runner)
            result = probe.probe(m.ToolProbeSpec("tool", ("tool",), ("--version",)))
            import hashlib
            self.assertEqual("AVAILABLE", result.status)
            self.assertEqual(hashlib.sha256(b"binary-v1").hexdigest(), result.executable_sha256)
            self.assertEqual("tool 1.2.3", result.version_output)

    def test_catalog_is_governed_and_non_promoted(self):
        inventory = m.integration_inventory()
        ids = {x["id"] for x in inventory["candidates"]}
        self.assertIn("github-public-intelligence-api", ids)
        self.assertIn("osv-api", ids)
        self.assertIn("frida-observation-only", ids)
        self.assertTrue(all(x["benchmark_required"] for x in inventory["candidates"]))


if __name__ == "__main__":
    unittest.main()
