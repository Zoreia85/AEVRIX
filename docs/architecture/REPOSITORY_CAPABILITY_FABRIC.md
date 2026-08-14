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

## Ollama local-model adapter

`AEVRIX.Remote.Capabilities` contains a native BCL-only REST adapter for an Ollama-compatible local endpoint.

Default policy:

- loopback endpoint only;
- no implicit model download or pull;
- no credentials in endpoint URIs;
- bounded request timeout;
- bounded response size;
- `stream=false`;
- JSON-only analysis contract;
- model output remains an untrusted `ModelAnalysisCandidate`;
- the existing `OrchestratorJudge` remains responsible for evidence-subset and promotion decisions.

Remote model endpoints require an explicit opt-in and remain subject to the same trust boundary.

## MCP registry

MCP servers are fail-closed. A connectable descriptor requires:

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

## Coding-agent backends

Coding agents are isolated workers, not trusted brains.

Runnable workers require:

- approved and pinned source;
- Container or VirtualMachine isolation;
- explicit project roots;
- no unrestricted host filesystem mount.

`LocalProcess` is intentionally not runnable under the default policy. This reflects the security lesson from agent platforms that explicitly warn that unsandboxed agents may access the host filesystem.

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

Native Windows CI compiled `AEVRIX.Remote.Capabilities` and executed the existing Windows Core, Remote Security and Remote Orchestration suites. The Ollama adapter is currently tested through a controlled HTTP transport fixture; this is not yet evidence of a live Ollama installation or a real local model inference session.

## Next gates

1. live Ollama health/model discovery and controlled local inference test on an approved test host;
2. signed capability approvals and SBOM/dependency-risk evidence;
3. GitHub metadata ingestion into `RepositoryObservation` with pinned commit evidence;
4. MCP transport adapter with per-call audit records;
5. sandboxed OpenHands-compatible worker adapter;
6. visual Investigation Flow Studio;
7. Reconstruction Studio sandboxed preview + behavioral/visual comparator;
8. authorized resilient web parser with bypass/evasion paths unavailable;
9. Windows native application/runtime tests beyond library CI;
10. Android emulator and Apple simulator gates when corresponding material applications exist.

Passing an architectural or unit-test gate does **not** make the AEVRIX product homologated.
