# Local AI evaluation record

2026-10-08 status: **NOT COMMISSIONED**. The deployment host and model remain undecided by the owner. No actual model latency, VRAM/RAM consumption, license suitability or operator usefulness result is available. Local inference stays disabled by default.

The [seed catalogue](local-ai-evaluation-cases.json) contains 18 labeled synthetic situations in English, Hebrew and Russian (54 cases). It covers material uncertainty, tool capacity, zero tools, legacy exception, changed support, four loader/verification situations, quantities, duplicate observations, connection versus freshness, absent assignment, empty/DST intervals and untrusted/revoked documents. Each case has an exact expected claim, source fixture identity and explicit synthetic limitation. These are an initial adapter corpus, not 54 human-approved shop questions or evidence that every described domain state is supplied by today's evidence endpoint.

`LocalAiEvaluationTests` validates claim preservation locally. Its optional real-candidate evaluation runs every case three times through the production Ollama adapter and validator, using **only synthetic evidence**, without a factory database. It records accepted/rejected results, bounded timings and runtime token metadata. It does not download a model. Run it separately for at least two reviewed candidates:

```powershell
./scripts/evaluate-local-ai.ps1 -ManifestPath C:\AI-Pilot\candidate-a.json -OutputPath artifacts/local-ai/candidate-a-results.json
```

Use a copy of the manifest with status `CANDIDATE`, populated `runtime.version`, `selectedModel.modelId`, `selectedModel.digest` and the token budgets. Keep provenance, license review and quantization in the same manifest. The runtime must already be running locally with cloud features disabled. Compare equal budgets and record any configuration differences. The launcher requires `APPROVED`; a candidate evaluation environment must therefore be provisioned separately by its operator.

Before acceptance, replace/extend the seed situations with 50–100 human-labeled factory questions and representative coherent Server bundles, including mixed-direction real shop vocabulary and authorized/unauthorized history. Reviewers must judge whether organizing existing claims adds value; the model cannot translate or create new narrative. Report each candidate's:

| Measure | Current result / required evidence |
|---|---|
| Exact model/runtime/digest/license | Unselected; populate manifest and verify local metadata |
| Citation and identifier correctness | Validator enforced; actual candidate repetitions not run |
| Deterministic numbers | Server-derived; reconcile real bundles with normal APIs |
| Unsupported actionable claims | Free model prose prohibited; review input/source interpretation too |
| Human correct/useful rate | Not measured; ≥90% on answerable questions required |
| Insufficient-evidence behavior | Synthetic fixtures present; human review required |
| Retrieval relevance | Literal text/ID matching; no semantic-quality measurement claimed |
| Cold and warm latency | Unmeasured; mark cold process/model state explicitly rather than calling repetition 0 cold |
| Memory/VRAM and concurrent ingestion lag | Unmeasured on target host |
| External network unavailable / reboot | Not commissioned |
| UI navigation and time saved | Awaiting planner/Tool Room/setup users |

Human go/no-go fields: reviewer, date, intent, question/case ID, expected source IDs, observed output, useful/correct/unsafe classification, correction, and decision. Keep real logs, manifests containing local paths, model artifacts and generated reports under ignored local storage. The pilot must not be declared passed from the seed validator or an empty candidate manifest.
