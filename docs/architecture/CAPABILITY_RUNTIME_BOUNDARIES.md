# AEVRIX Capability Runtime Boundaries

Status: **development / NOT_HOMOLOGATED**

This document defines the runtime trust boundaries for external models, MCP servers and sandboxed agent backends.

## Core principle

AEVRIX owns orchestration, evidence, policy, Council/Judge decisions, capability health, provenance and promotion. External runtimes provide bounded capabilities. They do not acquire decision authority merely because they can reason, execute code or expose tools.

## 1. Model providers

Model providers return `ModelAnalysisCandidate` objects only.

Required controls:

- provider approval;
- active health observation;
- stale-provider exclusion;
- bounded timeout and payload sizes;
- candidate confidence/risk metadata;
- evidence identifiers restricted to the governed task;
- Judge validation before promotion;
- quarantine cannot be released by a successful health probe alone.

### Ollama

The native Ollama adapter is local-first and uses loopback by default. `OllamaCapabilityHealthProbe` verifies both runtime reachability and presence of the configured model. A reachable Ollama runtime without the configured model is `Unavailable`, not healthy.

No model is pulled implicitly.

## 2. MCP servers

AEVRIX implements the modern MCP **2026-07-28 Streamable HTTP** request model as a separate governed transport.

Runtime behavior:

- one independent POST per request;
- `MCP-Protocol-Version: 2026-07-28` on every request;
- `Mcp-Method` and applicable `Mcp-Name` headers;
- `application/json` or request-scoped `text/event-stream` responses;
- JSON-RPC request/response id correlation;
- bounded response bytes and bounded SSE event count;
- server-side independent requests on the response SSE stream are rejected;
- no legacy protocol sessions or persistent GET stream are assumed;
- non-success HTTP cannot smuggle a successful JSON-RPC result;
- JSON-RPC errors are preserved as protocol errors.

### `x-mcp-header`

Tool header annotations are treated as an untrusted schema extension and validated before use:

- header names must be valid HTTP field-name tokens;
- header names are unique case-insensitively;
- only `string`, `integer` and `boolean` properties can be mirrored;
- `number`, arrays, objects and unsupported forms are rejected;
- unsafe/ambiguous header values use the protocol Base64 sentinel encoding;
- malformed individual tools are excluded from the accepted catalog.

### MCP health

`McpCapabilityHealthProbe` performs a read-only `tools/list` against an already approved server:

- valid reachable catalog => `Healthy`;
- reachable catalog containing rejected tool schemas => `Degraded`;
- transport/protocol failure => `Unavailable`.

The observation feeds `CapabilityBroker`, so a server can be removed from routing without modifying its registration.

## 3. Sandboxed coding-agent backends

Coding agents are execution workers, not trusted brains.

`SandboxAgentBackendClient` defines an AEVRIX-owned contract that third-party engines can sit behind.

A backend is usable only when `AgentBackendDescriptor.CanRun()` passes:

- source provenance present;
- explicit approval;
- Container or VirtualMachine isolation;
- no unrestricted host filesystem mount;
- bounded project-root allowlist.

`LocalProcess` is not accepted by the default execution policy.

### Submission boundary

AEVRIX submits an objective, governed evidence ids, project root and explicit policy. It does **not** hand the worker implicit authority over arbitrary host paths.

The requested project root:

- must be absolute;
- must be traversal-free;
- must be equal to or below an approved root;
- is normalized before transmission.

The request carries the exact isolation mode, host-filesystem prohibition, network policy and maximum runtime.

### Result boundary

Backend results are rejected unless their isolation attestation matches the approved request.

A successful result requires:

- matching job id;
- matching project root;
- matching approved isolation;
- no host filesystem mount;
- no unexpected network expansion;
- relative traversal-free changed-file list;
- bounded evidence ids and summary;
- SHA-256 artifact-manifest identifier.

The manifest hash provides an immutable identifier for the produced artifact set. It is not, by itself, proof that the patch is correct; independent AEVRIX validation remains required.

## 4. Capability health monitor

`CapabilityHealthMonitor` is the common active-health plane.

Properties:

- bounded concurrent probing;
- deterministic provider ordering;
- provider identity validation;
- probe exceptions converted to `Unavailable` observations;
- cancellation propagated intentionally;
- observations fed directly to `CapabilityBroker`;
- quarantine has higher authority than health recovery.

This allows the orchestration layer to maintain primary/backup providers without assuming a dependency remains available indefinitely.

## 5. Required next gates

1. live Ollama runtime + real loaded-model integration test;
2. real approved MCP server interoperability test against protocol 2026-07-28;
3. MCP per-operation immutable audit records;
4. real container/VM agent-backend implementation with isolation verification from outside the worker;
5. sandbox patch application into a disposable checkout, followed by compiler/tests/security scan before promotion;
6. SBOM and provenance binding for every executable external adapter;
7. signed approval policy and revocation workflow;
8. full Windows desktop runtime/installer test beyond library CI.

Passing unit and architectural gates does not make the AEVRIX product homologated.
