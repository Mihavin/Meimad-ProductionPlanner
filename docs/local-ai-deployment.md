# Local AI deployment and pilot

Status, 2026-10-08: application adapter and deterministic evidence are implemented locally. The owner left the target Windows host/GPU/RAM undecided and requested disabled-by-default operation. No model is selected, downloaded, commissioned or recommended by this document. The [manifest example](local-ai-model-manifest.example.json) deliberately contains no production defaults.

## Implemented boundary

The Server reads production facts through existing services, constructs bounded evidence, and accepts only a complete permutation of its own claim templates from Ollama. Model-generated prose, commands, links and quantities are never displayed. This first adapter can organize facts but does not translate or freely explain them. Human language usefulness must be evaluated with that limitation. Historical document excerpts are visibly untrusted reference material.

Deterministic evidence includes current scoped Timeline conflicts/package identities and clearly separated current health, workflow counts and completed-cycle timing samples. Timing follows the existing Timeline Server-receipt basis and completion-window attribution; it is not total shift occupancy. Shop and Operation expose the same bounded assistance lifecycle. Profile the extra forecast calculation under representative load before enabling a model; deterministic evidence availability alone is not pilot acceptance.

One worker, eight queued requests and one active request per user bound inference. The loopback-only HTTP adapter checks the exact installed model digest and runtime version before each request. It never pulls a model. Server-held metadata records intent, evidence IDs/versions, outcome and available token/latency metrics; questions/prompts/answer bodies are not retained on disk. Explicit feedback corrections of up to 500 characters are retained separately. Request metadata has no automatic deletion policy yet; the plan's proposed 30-day retention remains unapproved. Corpus content is a rebuildable approved-text index; removing roots clears it and saved document answers are rechecked before display.

## Prepare a target host

1. Run `scripts/inventory-local-ai-host.ps1` on the proposed host. It writes local inventory only. Record dedicated VRAM from the vendor utility, intended service identity, storage ACLs and expected concurrent load separately. Development-PC inventory does not approve that PC for production.
2. Review at least two suitable instruction-model artifacts and their licenses, provenance, quantization and exact digests. Acquire approved runtime/model files in a separate controlled acquisition step; transfer them to the isolated host. Store weights outside the repository and production database backups. Record reproducible artifact locations and checksums in a copy of the manifest.
3. Use a pinned portable runtime under the chosen identity. Review automatic-update behavior for the chosen distribution. `scripts/start-pinned-local-ai.ps1` requires an approved executable hash, binds loopback, disables cloud features and starts only the existing runtime. It does not install a Windows Service. Configure the approved service/task host and recovery policy separately, then test logout/reboot and that identity's model-folder access. Do not expose port 11434 on the LAN.
4. Verify installed model name/digest using the local `/api/tags` response and runtime version through `/api/version`. Set the matching `LocalAi` section through the Server's existing deployment configuration mechanism. Keep `Enabled=false` during setup. No client can choose an endpoint or model. A factory-LAN model host would require a later explicit deployment change; the implemented adapter accepts loopback only.

Example **disabled** Server settings:

```json
{
  "LocalAi": {
    "Enabled": false,
    "BaseUrl": "http://127.0.0.1:11434/",
    "ModelId": "",
    "ModelDigest": "",
    "RuntimeVersion": "",
    "RequestTimeoutSeconds": 60,
    "MaxQueueWaitSeconds": 30,
    "MaxInputTokens": 8192,
    "MaxOutputTokens": 1024,
    "ForecastTimeoutSeconds": 3
  }
}
```

The runtime's documented local-only setting is `OLLAMA_NO_CLOUD=1`; model storage can be set with `OLLAMA_MODELS`. The launcher also caps runtime parallelism at one. Recheck these controls against the exact runtime being commissioned. See [official configuration FAQ](https://docs.ollama.com/faq), [Windows deployment](https://docs.ollama.com/windows), and [installed model metadata](https://docs.ollama.com/api/tags), checked 2026-10-08.

Optional Timeline evidence has a separate `ForecastTimeoutSeconds` budget (default 3, permitted 1–10), also active with inference disabled. On expiry the answer keeps current readiness/recorded facts and explicitly reports unavailable forecast evidence. It does not infer a conflict-free plan. Timed-out source rechecks return `CHECK_UNAVAILABLE`; caller cancellation still cancels the whole request. Increasing this budget adds Planner CPU work and must be measured before pilot use.

## Evaluate and enable

Use isolated representative data and the evaluation catalogue/report procedure. Record cold and warm latency separately, process working set/VRAM, timeout/rejection rates, identifier/citation correctness and ingestion lag under normal Planner load. The warm blocker target remains p95 ≤15 seconds; zero fabricated/unauthorized identities and exact deterministic numbers are required. Human reviewers must rate at least 90% of answerable fixtures correct/useful, with no invented answer on insufficient-evidence fixtures. Record failed cases as well as passes. Unit tests and claim-schema validity do not establish this acceptance.

Repeat with external networking unavailable. Exercise missing model, changed digest, runtime stop, oversized/malformed response, queue saturation, cancellation, Server restart, revoked corpus, stale Run/package evidence and permission changes. Verify no model response changes planning. The rollout gate also requires the normal Server/Windows regression, complete recovery rehearsal and measured Planner ingestion impact.

After recorded acceptance, enable AI from the Windows client: Integrations → Approved documents → *Local AI* → **Check local runtime**, choose the tested model, **Save AI settings** with *Enable local AI* ticked (owner request 2026-10-09; Server-held, versioned, logged, applied without a restart and at every start). The configuration file's `Enabled` and pins are only the values in force until that setting is saved; `BaseUrl` and the token budgets still come from the file. Set the resource limits there too: CPU threads (at most cores − 1; the runtime's `num_thread`), unload after idle seconds (`keep_alive`) and a model memory budget in MB; the Server never loads a model over the budget. Host-side, keep the runtime to one model and one request at a time (Ollama: `OLLAMA_MAX_LOADED_MODELS=1`, `OLLAMA_NUM_PARALLEL=1`); Planner serializes its own requests already. Begin with a few Windows users and selected contexts; review usefulness and time saved for the agreed pilot period. In Integrations → Approved documents, administrators can **Load inference control**, **Suspend inference**, or **Resume commissioned inference**. Suspension persists across restart, cancels admitted AI work and keeps new deterministic explanations available. Resume cannot override disabled Server configuration. Corpus approval and inference control are independent actions.

## Rollback and removal

Suspend inference first. Restore the previously tested runtime/model/configuration manifest as one set, then re-run offline smoke checks before resuming. A model/runtime mismatch produces deterministic fallback. For removal, keep Planner `Enabled=false`, stop/remove only the dedicated AI task/service through its host's normal management tools, and remove model files only after checking their explicit storage directory. Removing AI runtime/weights does not require downgrading Planner's database. Clear approved roots through the UI to remove indexed document copies. Planner schema rollback uses the complete recovery procedure and matching prior binaries, never manual SQLite edits.

Open acceptance: selected host, two actual model evaluations/license reviews, deployed identity/reboot, physical load measurements, multilingual usefulness and monitored pilot. None is claimed complete.
