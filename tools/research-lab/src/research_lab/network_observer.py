from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit


_SENSITIVE_QUERY_KEYS = {
    "access_token",
    "apikey",
    "api_key",
    "auth",
    "authorization",
    "code",
    "id_token",
    "password",
    "refresh_token",
    "secret",
    "session",
    "sessionid",
    "token",
}


def sanitized_url(url: str) -> str:
    parsed = urlsplit(url)
    query = []
    for key, value in parse_qsl(parsed.query, keep_blank_values=True):
        if key.lower().replace("-", "_") in _SENSITIVE_QUERY_KEYS:
            query.append((key, "[REDACTED]"))
        else:
            query.append((key, value))
    return urlunsplit((parsed.scheme, parsed.netloc, parsed.path, urlencode(query), ""))


def _schema(value: object, *, path: str = "$", depth: int = 0, limit: int = 1500) -> list[dict[str, object]]:
    out: list[dict[str, object]] = []

    def visit(node: object, node_path: str, level: int) -> None:
        if level > 6 or len(out) >= limit:
            return
        if isinstance(node, dict):
            keys = sorted(str(key) for key in node.keys())[:120]
            out.append({"path": node_path, "type": "object", "keys": keys})
            for key in keys:
                if key in node:
                    visit(node[key], f"{node_path}.{key}", level + 1)
        elif isinstance(node, list):
            out.append({"path": node_path, "type": "array", "length": len(node)})
            if node:
                visit(node[0], f"{node_path}[0]", level + 1)
        else:
            out.append({"path": node_path, "type": type(node).__name__})

    visit(value, path, depth)
    return out


@dataclass(frozen=True, slots=True)
class NetworkEvent:
    method: str
    url: str
    status: int
    resource_type: str
    content_type: str = ""
    first_party: bool = True
    json_schema: tuple[dict[str, object], ...] = ()
    pagination_hints: tuple[str, ...] = ()

    @property
    def endpoint_key(self) -> str:
        parsed = urlsplit(self.url)
        raw = f"{self.method.upper()} {parsed.scheme.lower()}://{parsed.netloc.lower()}{parsed.path}"
        return hashlib.sha256(raw.encode()).hexdigest()


@dataclass(slots=True)
class EndpointCatalog:
    events: list[NetworkEvent] = field(default_factory=list)

    def add_json_response(
        self,
        *,
        method: str,
        url: str,
        status: int,
        resource_type: str,
        content_type: str,
        payload_text: str,
        first_party: bool,
    ) -> NetworkEvent:
        schema: tuple[dict[str, object], ...] = ()
        pagination: list[str] = []
        if "json" in content_type.lower():
            try:
                payload = json.loads(payload_text)
                schema = tuple(_schema(payload))
                schema_text = json.dumps(schema, ensure_ascii=False).lower()
                for marker in ("page", "pages", "page_size", "pagesize", "total", "offset", "limit", "cursor", "next"):
                    if marker in schema_text:
                        pagination.append(marker)
            except json.JSONDecodeError:
                pass
        event = NetworkEvent(
            method=method.upper(),
            url=sanitized_url(url),
            status=status,
            resource_type=resource_type,
            content_type=content_type,
            first_party=first_party,
            json_schema=schema,
            pagination_hints=tuple(sorted(set(pagination))),
        )
        self.events.append(event)
        return event

    def unique_endpoints(self, *, first_party_only: bool = True) -> dict[str, NetworkEvent]:
        result: dict[str, NetworkEvent] = {}
        for event in self.events:
            if first_party_only and not event.first_party:
                continue
            result.setdefault(event.endpoint_key, event)
        return result

    @property
    def open_pagination_candidates(self) -> int:
        return sum(1 for event in self.unique_endpoints().values() if event.pagination_hints)
