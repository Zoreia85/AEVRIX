# Repository Capability Fabric

Status: **development / NOT_HOMOLOGATED**

AEVRIX may use public open-source projects as runtimes, adapters, discovery sources, pattern references or isolated worker backends. No upstream project becomes the AEVRIX brain or gains implicit execution authority.

## System-of-record rule

The AEVRIX brain remains responsible for:

- Evidence -> Blueprint provenance;
- candidate knowledge and QIR state;
- Council/Judge decisions;
- independent validation and promotion;
- policy enforcement;
- capability selection;
- active health and quarantine;
- audit trail and release gates.

Third-party outputs are evidence or untrusted candidates until AEVRIX validates them.

## Provenance gates

Executable integrations require all of the following before runtime eligibility:

1. canonical repository identity;
2. SPDX license recorded and rechecked;
3. security review approved;
4. exact Git revision pinned;
5. governed source-content SHA-256;
6. explicit runtime allowlisting;
7. explicit allowed/denied capabilities;
8. bounded filesystem and network permissions;
9. no embedded credential values;
10. independent test evidence.

`RepositoryProvenanceVerifier` compares fresh observations with the governed record and reports identity, URL, license, archival, revision and content-hash drift. Executable records fail closed when approval, pinning or hashing is missing.

Discovery lists never confer execution authority on the projects they contain.

## Model runtime fabric

`AEVRIX.Remote.Capabilities` contains a native BCL-only REST adapter for an Ollama-compatible endpoint plus adaptive provider routing.

Default Ollama policy:

- loopback endpoint only;
- no implicit model download or pull;
- no credentials in endpoint URIs;
- bounded request timeout;
- bounded response size;
- `stream=false`;
- JSON-only analysis contract;
- model output remains an untrusted `ModelAnalysisCandidate`;
- the existing `OrchestratorJudge` remains responsible for evidence-subset and promotion decisions.

`CapabilityBroker` ranks approved providers using quality, reliability, latency and health observations. `AdaptiveModelCouncilProvider` can fail over between governed providers while recording outcomes.

`CapabilityHealthMonitor` now provides active bounded probing. `OllamaCapabilityHealthProbe` declares the provider healthy only when the runtime is reachable **and the configured model is actually present**. Quarantine cannot be silently released by a health probe.

Remote model endpoints require an explicit opt-in and remain subject to the same trust boundary.

## MCP registry and transport

MCP servers remain fail-closed. A connectable descriptor requires:

- approved state;
- source repository identity;
- SPDX license;
- pinned Git revision;
- content SHA-256;
- explicit capabilities;
- secret *names* only, never secret values;
- explicit filesystem roots;
- HTTPS remotely, or HTTP only on loopback.

Capabilities associated with bypass/evasion, implicit plugin execution, automatic server execution, automatic credential use or unrestricted host filesystem access are centrally denied.

`McpStreamableHttpClient` implements the governed AEVRIX transport for MCP protocol revision `2026-07-28`:

- one independent POST per request;
- mandatory protocol/method/name metadata;
- JSON-RPC request/response correlation;
- `application/json` and request-scoped SSE responses;
- bounded body/event limits;
- no legacy sessionful GET stream;
- no independent server JSON-RPC requests on response SSE;
- HTTP status and JSON-RPC semantic cross-checks;
- validated `x-mcp-header` declarations;
- Base64 sentinel encoding for unsafe/ambiguous mirrored header values;
- malformed individual tool schemas excluded from the usable catalog.

`McpCapabilityHealthProbe` performs a read-only tools/list against an already-approved server. A clean catalog is Healthy, a reachable server with rejected tool schemas is Degraded, and transport/protocol failure is Unavailable.

## Coding-agent backends

Coding agents are isolated workers, not trusted brains.

Runnable workers require:

- approved and pinned source;
- Container or VirtualMachine isolation;
- explicit project roots;
- no unrestricted host filesystem mount.

`LocalProcess` is intentionally not runnable under the default policy.

`SandboxAgentBackendClient` introduces an AEVRIX-owned backend contract suitable for future OpenHands-compatible or other coding engines. The external engine receives a bounded objective, evidence ids, approved project root and explicit isolation/network/runtime policy.

Results are rejected unless they return a matching isolation attestation. Successful jobs also require a SHA-256 artifact-manifest identifier. Changed-file paths must be relative, traversal-free and bounded. A sandbox result is still not promoted until AEVRIX independently validates its artifacts.

## Curated upstream roles

| Upstream | AEVRIX role |
|---|---|
| `ollama/ollama` | optional local-model runtime |
| `OpenHands/OpenHands` | sandboxed coding-agent architecture / optional backend candidate |
| `langflow-ai/langflow` | workflow, API/MCP and observability reference |
| `nexu-io/open-design` | reconstruction studio, sandboxed preview and agent-adapter reference |
| `Shubhamsaboo/awesome-llm-apps` | agent/RAG/multi-agent pattern and evaluation corpus |
| `punkpeye/awesome-mcp-servers` | MCP discovery seed only |
| `sindresorhus/awesome` | repository discovery seed only |
| `public-apis/public-apis` | public API discovery seed only |
| `D4Vinci/Scrapling` | authorized resilient parsing/web-evidence reference; bypass/evasion capabilities denied |
| `ripienaar/free-for-dev` | infrastructure discovery reference only |

## Evidence obtained in this development cycle

Native Windows CI compiled `AEVRIX.Remote.Capabilities` and executed the Windows Core, Remote Security and Remote Orchestration suites with the model fabric, active-health plane, MCP transport/health probe and sandbox-agent contract included.

The current passing Windows gate is:

- Windows Core: 32/32;
- Remote Security: 4/4;
- Remote Orchestration: 61/61;
- total: 97/97.

The Source Policy gate also passed for the same code revision.

The Ollama adapter remains tested through controlled HTTP fixtures. This is not yet evidence of a live Ollama installation or real local model inference. Likewise, MCP and sandbox-agent tests currently validate the AEVRIX-side protocol and policy contracts; real third-party interoperability remains a separate gate.

## Next gates

1. live Ollama health/model discovery and controlled local inference test on an approved test host;
2. real approved MCP 2026-07-28 interoperability test;
3. real Container/VM coding-agent backend with external isolation verification;
4. immutable per-capability execution audit records;
5. disposable-checkout patch promotion pipeline: agent output -> compiler/tests/security -> Judge -> controlled promotion;
6. signed capability approvals and SBOM/dependency-risk evidence;
7. GitHub metadata ingestion into `RepositoryObservation` with pinned commit evidence;
8. visual Investigation Flow Studio;
9. Reconstruction Studio sandboxed preview + behavioral/visual comparator;
10. authorized resilient web parser with bypass/evasion paths unavailable;
11. Windows native application/runtime tests beyond library CI;
12. Android emulator and Apple simulator gates when corresponding material applications exist.

Passing an architectural or unit-test gate does **not** make the AEVRIX product homologated.
