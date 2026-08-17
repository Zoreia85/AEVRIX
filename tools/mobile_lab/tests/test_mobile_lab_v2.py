from __future__ import annotations

import hashlib
import unittest
from datetime import datetime, timezone

from tools.mobile_lab.algorithm_inference import (
    NumericObservation,
    infer_numeric_rule,
    select_discriminating_case,
)
from tools.mobile_lab.differential import compare_numeric
from tools.mobile_lab.dynamic_lab import DisposableEnvironmentSpec, DisposableLabRunner
from tools.mobile_lab.observation import ObservationRecord


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def observation(environment_id: str = "env-1", platform: str = "android") -> ObservationRecord:
    return ObservationRecord(
        timestamp=datetime.now(timezone.utc).isoformat(),
        session_id="session-1",
        sequence=0,
        platform=platform,
        environment_id=environment_id,
        previous_state_id="state-launch",
        action="tap:continue",
        next_state_id="state-home",
        screenshot_sha256=digest(b"screen"),
        ui_tree_sha256=digest(b"tree"),
        metrics={"cpu_percent": 4.2, "rss_mb": 81.0},
    )


class ObservationTests(unittest.TestCase):
    def test_observation_has_deterministic_evidence_id(self) -> None:
        item = observation()
        self.assertEqual(item.evidence_id, item.evidence_id)
        self.assertTrue(item.evidence_id.startswith("obs_"))

    def test_timestamp_requires_timezone(self) -> None:
        with self.assertRaises(ValueError):
            ObservationRecord(
                timestamp="2026-08-17T10:00:00",
                session_id="s",
                sequence=0,
                platform="android",
                environment_id="e",
                previous_state_id=None,
                action="launch",
                next_state_id=None,
            )

    def test_invalid_hash_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            ObservationRecord(
                timestamp=datetime.now(timezone.utc).isoformat(),
                session_id="s",
                sequence=0,
                platform="android",
                environment_id="e",
                previous_state_id=None,
                action="launch",
                next_state_id=None,
                screenshot_sha256="not-a-hash",
            )


class FakeAdapter:
    def __init__(self, fail_destroy: bool = False) -> None:
        self.calls: list[str] = []
        self.fail_destroy = fail_destroy

    def create(self, spec: DisposableEnvironmentSpec) -> str:
        self.calls.append("create")
        return "env-1"

    def boot(self, environment_id: str) -> None:
        self.calls.append("boot")

    def destroy(self, environment_id: str) -> None:
        self.calls.append("destroy")
        if self.fail_destroy:
            raise RuntimeError("destroy failed")


class DynamicLabTests(unittest.TestCase):
    def spec(self) -> DisposableEnvironmentSpec:
        return DisposableEnvironmentSpec(
            platform="android",
            os_version="35",
            device_profile="pixel",
            resolution="1080x2400",
            authorized=True,
        )

    def test_cleanup_runs_after_success(self) -> None:
        adapter = FakeAdapter()
        result = DisposableLabRunner(adapter).run(self.spec(), lambda _, env: [observation(env)])
        self.assertEqual(result.status, "COMPLETED")
        self.assertTrue(result.cleanup_completed)
        self.assertEqual(adapter.calls, ["create", "boot", "destroy"])

    def test_cleanup_runs_after_probe_failure(self) -> None:
        adapter = FakeAdapter()

        def fail(_, __):
            raise RuntimeError("probe failed")

        result = DisposableLabRunner(adapter).run(self.spec(), fail)
        self.assertEqual(result.status, "FAILED")
        self.assertTrue(result.cleanup_completed)
        self.assertEqual(result.error_type, "RuntimeError")
        self.assertEqual(adapter.calls, ["create", "boot", "destroy"])

    def test_cleanup_failure_is_visible(self) -> None:
        adapter = FakeAdapter(fail_destroy=True)
        result = DisposableLabRunner(adapter).run(self.spec(), lambda _, env: [observation(env)])
        self.assertEqual(result.status, "FAILED_CLEANUP")
        self.assertFalse(result.cleanup_completed)

    def test_authorization_is_mandatory(self) -> None:
        adapter = FakeAdapter()
        spec = DisposableEnvironmentSpec(
            platform="android", os_version="35", device_profile="pixel", resolution="1080x2400"
        )
        with self.assertRaises(PermissionError):
            DisposableLabRunner(adapter).run(spec, lambda _, env: [])
        self.assertEqual(adapter.calls, [])


class AlgorithmInferenceTests(unittest.TestCase):
    def linear_observations(self):
        return [NumericObservation({"x": float(x)}, 2.0 * x + 3.0) for x in range(-4, 5)]

    def test_affine_rule_is_highly_probable_not_proven(self) -> None:
        report = infer_numeric_rule(self.linear_observations())
        self.assertEqual(report.status, "HIGHLY_PROBABLE")
        self.assertEqual(report.selected.family, "affine")
        self.assertAlmostEqual(report.selected.intercept, 3.0)
        self.assertAlmostEqual(report.selected.coefficients["x"], 2.0)
        self.assertNotIn("PROVEN", report.status)

    def test_exhaustive_declared_domain_can_be_proven_within_domain(self) -> None:
        report = infer_numeric_rule(self.linear_observations(), exhaustive_declared_domain=True)
        self.assertEqual(report.status, "PROVEN_WITHIN_DECLARED_DOMAIN")

    def test_ambiguous_models_request_discriminating_input(self) -> None:
        report = infer_numeric_rule([NumericObservation({"x": 1.0}, 2.0)])
        self.assertEqual(report.status, "INFERRED_AMBIGUOUS")
        case = select_discriminating_case(report, [{"x": 0}, {"x": 1}, {"x": 2}])
        self.assertIsNotNone(case)
        self.assertIn(case["x"], {0.0, 2.0})

    def test_unexplained_non_linear_data_is_not_forced_into_linear_family(self) -> None:
        obs = [NumericObservation({"x": float(x)}, float(x * x)) for x in range(-3, 4)]
        report = infer_numeric_rule(obs)
        self.assertEqual(report.status, "UNEXPLAINED")
        self.assertIsNone(report.selected)


class DifferentialTests(unittest.TestCase):
    def test_numeric_equivalence_respects_tolerance(self) -> None:
        close = compare_numeric(100.0, 100.00000001, abs_tolerance=1e-6)
        far = compare_numeric(100.0, 100.1, abs_tolerance=1e-6, rel_tolerance=1e-6)
        self.assertTrue(close.equivalent)
        self.assertFalse(far.equivalent)


if __name__ == "__main__":
    unittest.main()
