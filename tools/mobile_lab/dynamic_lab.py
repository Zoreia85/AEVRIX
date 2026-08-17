from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Protocol, Sequence

from .observation import ObservationRecord


@dataclass(frozen=True)
class DisposableEnvironmentSpec:
    platform: str
    os_version: str
    device_profile: str
    resolution: str
    locale: str = "en-US"
    network_mode: str = "isolated"
    reset_policy: str = "fresh"
    authorized: bool = False

    def validate(self) -> "DisposableEnvironmentSpec":
        if not self.authorized:
            raise PermissionError("dynamic lab requires an explicitly authorized test target")
        if self.platform not in {"android", "ios"}:
            raise ValueError("platform must be android or ios")
        if self.network_mode not in {"offline", "isolated", "controlled"}:
            raise ValueError("network_mode must be offline, isolated, or controlled")
        if self.reset_policy != "fresh":
            raise ValueError("v0.2 only permits fresh disposable environments")
        for name in ("os_version", "device_profile", "resolution", "locale"):
            if not getattr(self, name).strip():
                raise ValueError(f"{name} is required")
        return self


class DisposableDeviceAdapter(Protocol):
    """Adapter boundary for governed emulator/simulator capabilities."""

    def create(self, spec: DisposableEnvironmentSpec) -> str: ...

    def boot(self, environment_id: str) -> None: ...

    def destroy(self, environment_id: str) -> None: ...


Probe = Callable[[DisposableDeviceAdapter, str], Sequence[ObservationRecord]]


@dataclass(frozen=True)
class LabRunResult:
    status: str
    environment_id: str | None
    cleanup_completed: bool
    observations: tuple[ObservationRecord, ...]
    error_type: str | None = None
    error_message: str | None = None

    def to_dict(self) -> dict:
        return {
            "schema_version": 1,
            "status": self.status,
            "environment_id": self.environment_id,
            "cleanup_completed": self.cleanup_completed,
            "observations": [item.to_dict() for item in self.observations],
            "error_type": self.error_type,
            "error_message": self.error_message,
        }


class DisposableLabRunner:
    """Enforces create -> boot -> probe -> destroy with cleanup in every path.

    This orchestrator deliberately contains no Android SDK or Apple tooling. Those
    implementations remain governed capability adapters and may only be attached
    after their lifecycle state permits it.
    """

    def __init__(self, adapter: DisposableDeviceAdapter) -> None:
        self._adapter = adapter

    def run(self, spec: DisposableEnvironmentSpec, probe: Probe) -> LabRunResult:
        spec = spec.validate()
        environment_id: str | None = None
        observations: tuple[ObservationRecord, ...] = ()
        cleanup_completed = False
        status = "FAILED"
        error_type: str | None = None
        error_message: str | None = None

        try:
            environment_id = self._adapter.create(spec)
            if not environment_id or not environment_id.strip():
                raise RuntimeError("adapter returned an empty environment_id")
            self._adapter.boot(environment_id)
            observed = tuple(probe(self._adapter, environment_id))
            for item in observed:
                if item.environment_id != environment_id:
                    raise ValueError("observation environment_id does not match the disposable environment")
                if item.platform != spec.platform:
                    raise ValueError("observation platform does not match environment spec")
            observations = observed
            status = "COMPLETED"
        except Exception as exc:
            error_type = type(exc).__name__
            error_message = str(exc)
        finally:
            if environment_id is not None:
                try:
                    self._adapter.destroy(environment_id)
                    cleanup_completed = True
                except Exception as exc:
                    cleanup_completed = False
                    status = "FAILED_CLEANUP"
                    if error_type is None:
                        error_type = type(exc).__name__
                        error_message = str(exc)

        if status == "COMPLETED" and not cleanup_completed:
            status = "FAILED_CLEANUP"

        return LabRunResult(
            status=status,
            environment_id=environment_id,
            cleanup_completed=cleanup_completed,
            observations=observations,
            error_type=error_type,
            error_message=error_message,
        )
