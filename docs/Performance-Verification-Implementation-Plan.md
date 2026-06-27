# Performance Verification — Implementation Plan

> **Status:** Largely implemented — see _Implementation status_ below
> **Audience:** Core team contributors
> **Implements:** [§9 Performance Verification](LSP-IDE-Support-Architecture.md#performance-verification),
> tasks [T1, T2, T3](LSP-IDE-Support-Architecture.md#11-non-feature-engineering-tasks)
> **Scope:** The two **adopted** verification elements only — **Layer 2** (end-to-end protocol
> benchmarks) and **Layer 4** (field instrumentation). Layers 1 (micro-benchmarks) and 3 (CI
> regression gating) remain deferred and are out of scope here, except where this plan deliberately
> leaves a seam for Layer 3 to plug into later.

---

## Implementation status (as built)

| Phase / item | Status | Where |
|---|---|---|
| **Layer 4 — field instrumentation (T3)** | ✅ implemented | `LSP.Server/Diagnostics/Performance/` (`IOperationDurationRecorder`, sampled `PerfSample`); wired into semanticTokens (manual route), completion, definition, diagnostics push |
| **T2 — corpus + structural-fingerprint drift test** | ✅ implemented | `tests/Performance/Corpus/` (committed: 50 features, 64 patterns, 1350 steps, 950/200/200 mix); `Benchmarks.Core/Corpus/`; `CorpusDriftTests` |
| **T1 — harness + interactive scenarios** | ✅ implemented | `Benchmarks.Core/Harness/BenchmarkLspHarness`, `Scenarios/InteractiveScenarios`; `Benchmarks` exe `run` command |
| **T1 — latency percentiles, reporters, reference-machine gating** | ✅ implemented | `Benchmarks.Core/Latency/`, `Reporting/`; JSON output is the Layer 3 baseline format |
| **T1 — cold-start batch scenario** | ✅ implemented | `Benchmarks.Core/Scenarios/BatchScenarios.ColdStartScanAsync` |
| **T1 — Roslyn / reflection discovery batch scenarios** | ⏳ deferred | reported as **skipped (not faked)** until a *built* corpus bindings assembly + connector is wired; see A3.2/§A1 |
| Binding-dependent interactive numbers (definition cache-hit, step completion) | ⚠️ run, but against an unprimed registry | representative bound-state numbers need the same built corpus assembly; the harness + measurement are correct |
| Layer 1 micro-benchmarks, Layer 3 CI gating | ⏳ deferred by design | seams left (JSON baseline format exists for Layer 3) |

**Honest gap:** the one piece not yet built is a **buildable corpus bindings assembly** (a `.csproj`
referencing Reqnroll, built + deployed next to the benchmark). It is the prerequisite for (a) the two
binding-discovery batch scenarios and (b) representative bound-state numbers for definition/step
completion. The source-only corpus is sufficient for the drift test, semantic tokens, keyword
completion, diagnostics push, and cold start — which run today.

---

## 0. What §9 actually asks for

The architecture commits to two verification layers and explains *why each is necessary and not
sufficient on its own*:

| Element | Architecture task | Confirms | Why the other layer can't |
|---|---|---|---|
| **Layer 2** — E2E protocol benchmarks against a pinned corpus on a reference machine | T1 (harness) + T2 (corpus) | The §9 P95 targets *as phrased* — measured at the protocol boundary, against a controlled workload, with absolute thresholds | Field data is uncontrolled: workload and hardware vary, so it can't confirm a *design target* |
| **Layer 4** — field instrumentation in the live handlers | T3 | Real-world P95 on real hardware/workspaces, where the "typical hardware" assumption is actually exercised | A synthetic corpus on one reference box can't prove the targets hold for real users |

Two design constraints from §9 shape everything below:

1. **Measure at the protocol boundary, end-to-end.** The interactive targets are phrased *"from last
   `didChange` event"*. Timing a service method (`SemanticTokenService.GetSemanticTokensAsync`) in
   isolation undercounts JSON-RPC serialization, transport, and MediatR/OmniSharp fan-out. Both
   layers must straddle the wire, not the service call.
2. **Absolute vs. relative.** Absolute pass/fail thresholds are asserted **only** on a designated
   reference machine. Everywhere else (dev boxes, shared CI) the harness *reports* numbers but does
   not gate on them. This keeps the door open for Layer 3 (relative regression gating) without
   re-plumbing.

---

## 1. What already exists that we build on

This is not greenfield. The seed for Layer 2 already ships in the spec suite:

- **[`LspServerHarness`](../tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Support/LspServerHarness.cs)**
  hosts the **real** server in-process over a `Nerdbank.Streams` `FullDuplexStream` pair and connects
  an OmniSharp `LanguageClient` to it. It already captures server→client traffic
  (`workspace/semanticTokens/refresh`, `reqnroll/semanticTokens`, `workspace/applyEdit`) with
  `TaskCompletionSource` signals and `WaitForXxxAsync` helpers. This is exactly the transport the
  benchmark driver needs — the work is to *generalise the wait-for-response capture* and drive it at
  volume, not to invent a host.
- **[`Program.ConfigureServer`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/Program.cs)** is
  deliberately transport-agnostic (the comment at line 73 calls this out): production uses stdio, the
  specs supply a pipe. The benchmark project supplies the same pipe.
- **Routing reality (matters for Layer 4):** the server uses **two** handler-registration styles:
  - **OmniSharp `AddHandler<T>`** for `completion`, `definition`, formatting, folding, document
    symbols, text sync — see
    [`AddStandardHandlers`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/LanguageServerOptionsExtensions.cs).
  - **Manual `OnRequest` / `OnNotification` delegates** for `semanticTokens/full`, references,
    go-to-step, code lens, rename, etc. — see `InitializeCustomProtocolRouting` in the same file.
  - Diagnostics are a **server push**, not a request:
    [`DiagnosticsPublishHandler`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Pipeline/DiagnosticsPublishHandler.cs)
    listens for `MatchCacheChangedNotification` and sends `textDocument/publishDiagnostics`.
  Any instrumentation that claims to cover the four interactive targets must cover **all three**
  shapes (manual request, OmniSharp request, server push). This is the central wrinkle of Part B.
- **[`ILspTelemetryService`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Telemetry/ILspTelemetryService.cs)**
  (`SendEvent`) and the **`IDeveroomLogger`** file-logging path
  ([`LspDeveroomLogger`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Logging/LspDeveroomLogger.cs)) are
  the two sinks §9 names for Layer 4 ("via the existing logging path … and optionally as a telemetry
  metric").
- **Corpus raw material:** the
  [`SampleProjectGenerator.Core`](../tests/Reqnroll.SampleProjectGenerator.Core) (project scaffolding,
  `LoremIpsum`) and the prebuilt
  [`BindingsFixture`](../tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs.BindingsFixture) give us a
  starting point for generating binding C# and feature text.

---

# Part A — Layer 2: end-to-end protocol benchmarks (T1 + T2)

## A1. Why a bespoke percentile driver, not BenchmarkDotNet

BenchmarkDotNet is the right tool for **Layer 1** micro-benchmarks (isolated, stateless, pure-compute
functions) — and §9 explicitly keeps Layer 1 deferred. It is the **wrong** tool for an end-to-end LSP
round-trip:

- The server is **stateful and async** (one live process, document buffers, a warmed match cache).
  BDN's pilot/warmup/multiple-invocation statistical model assumes a re-runnable, side-effect-free
  micro-op and fights a long-lived stateful server.
- We need **per-operation P50/P95/P99 over the wire**, which is a percentile-of-latency question, not
  a throughput/mean-with-variance question.

**Decision:** a bespoke console driver that starts the real server once, warms it, issues *N* requests
per operation, records each round-trip latency, and reports percentiles. The statistics are simple
(sorted-array percentiles); the value is in the realistic transport, corpus, and warm state.

> This mirrors the existing in-repo precedent that the spec harness already drives a real server over
> a real pipe — we are scaling that pattern up, not introducing a new framework.

## A2. New project: `Reqnroll.IdeSupport.LSP.Server.Benchmarks` (T1)

A `net10.0` **console** project under `tests/Performance/`. Console (not a test project) because it is
a measurement *tool* run on demand / on the reference machine, not an `xUnit` assertion run in the
normal `dotnet test` sweep (which would be noisy and slow).

```
tests/Performance/Reqnroll.IdeSupport.LSP.Server.Benchmarks/
  Reqnroll.IdeSupport.LSP.Server.Benchmarks.csproj
  Harness/
    BenchmarkLspHarness.cs      // generalised LspServerHarness: arbitrary request + wait-for-push
    LatencyRecorder.cs          // collects samples, computes P50/P95/P99/max, min, mean
    OperationResult.cs          // operation name, target ms, samples, percentiles, pass/fail
  Scenarios/
    InteractiveScenarios.cs     // semanticTokens, completion(kw/step), definition, diagnostics
    BatchScenarios.cs           // cold-start scan, Roslyn re-discovery, reflection discovery
  Reporting/
    ConsoleReporter.cs          // human-readable table
    JsonReporter.cs             // machine-readable results (seed for Layer 3 baselines)
  ReferenceMachine.cs           // gate: absolute thresholds only when designated
  Program.cs                    // CLI: --corpus <path> --iterations N --assert --out results.json
```

### A2.1 Harness generalisation

`BenchmarkLspHarness` factors the spec harness's mechanics into a reusable shape:

- Start the server over `FullDuplexStream` via `Program.ConfigureServer` (identical to the spec
  harness).
- Expose `Task<TResp> RequestAsync<TParams,TResp>(method, params)` wrapping the OmniSharp client's
  `SendRequest`, timed with a `Stopwatch` around the awaited round-trip (serialize → transport →
  handler → serialize → transport → deserialize).
- Expose a **timestamped server-push capture** so a `didChange` can be timed against the subsequent
  `textDocument/publishDiagnostics` (and `reqnroll/semanticTokens`) — generalising the existing
  `_pushSignal`/`WaitForPushAsync` plumbing to record *arrival time*, not just count.

### A2.2 What each operation measures (honouring "from last `didChange`")

| §9 target | Driver action | Latency captured |
|---|---|---|
| `semanticTokens/full` < 100 ms | send `didChange` for a corpus feature, then `semanticTokens/full` request | request round-trip (warm buffer) **+** a variant that measures change→tokens to honour the "from last didChange" phrasing |
| keyword completion (F7) < 50 ms | `completion` request at a line-start position | request round-trip |
| step completion (F8) < 150 ms | `completion` request mid-step (forces binding-set scan) | request round-trip |
| definition cache-hit (F5) < 100 ms | `definition` on a step whose match is already cached (warm) | request round-trip |
| `publishDiagnostics` push < 500 ms | `didChange` introducing an undefined step, then await the push for that URI/version | **end-of-debounce → push arrival** (see note) |
| Roslyn re-discovery (1 `.cs`) < 2 s | edit one binding `.cs`, await re-match settle | wall-clock |
| reflection discovery post-build < 10 s | trigger connector discovery against the built corpus assembly | wall-clock |
| cold-start scan < 30 s | fresh server + `initialize` + project-loaded notifications over the whole corpus, await first full match | wall-clock |

**Debounce note:** §9 phrases the diagnostics target as *"from end of debounce window"*. The driver
measures from the moment it stops typing (last `didChange`) to push arrival, then **reports both** the
raw value and the value minus the configured debounce interval, so the number is comparable to the
target without baking the debounce constant into the assertion. (Confirm the actual debounce source;
if there is no explicit debounce today, the raw `didChange → push` value *is* the measured quantity.)

### A2.3 Warmup, sample count, and percentiles

- **Warmup:** N (default 20) discarded iterations per interactive operation so JIT, the match cache,
  and document buffers are hot — the targets assume warm state ("cache hit").
- **Measured:** M (default 200) iterations per interactive operation; percentiles from the sorted
  sample array (`P95 = sample[ceil(0.95·M) − 1]`).
- **Batch operations** run a small count (default 3) since each is seconds-long; report min/median/max.
- Interactive operations cycle across **multiple corpus files** (not one hot file repeatedly) so the
  numbers reflect the corpus, not a single warmed document.

### A2.4 Reference-machine gating (absolute vs. report-only)

`ReferenceMachine.IsDesignated` reads an env var (`REQNROLL_PERF_REFERENCE_MACHINE=1`) or a
`--assert` CLI flag. Behaviour:

- **Designated / `--assert`:** compare each operation's P95 against its §9 target; non-zero exit on
  any breach (table marks ✅/❌).
- **Anywhere else:** print the table, write JSON, **always exit 0**. Numbers are informational.

This is the §9 rule ("absolute thresholds asserted on a designated reference machine, not shared CI
runners") expressed as one branch, and it is the seam Layer 3 later uses (compare JSON to a stored
baseline with a regression %, instead of to absolute targets).

### A2.5 Output

- **Console table:** operation, target, P50, P95, P99, max, verdict.
- **JSON** (`--out`): per-operation samples + percentiles + corpus manifest hash + machine id +
  timestamp. This file is the **Layer 3 baseline format** — building it now is the "Layer 3 becomes
  cheap once Layer 2 exists" payoff §9 calls out.

## A3. The pinned corpus (T2)

### A3.1 Requirements (from §9 "typical workspace conditions")

- ≤ **500** `.feature` files, ≤ **2,000** binding patterns.
- **Pinned & versioned** — the **committed corpus files** are the pinned artifact; every run reads
  the same bytes off disk, so results are comparable over time.
- **Regenerable** — a generator/curation script exists so the corpus can be *re-pinned* deliberately,
  but regeneration is **not** assumed to be byte-identical on demand (see A3.2.1).
- **Shape-pinned, not text-pinned** — what matters for benchmark validity is the corpus's *size and
  shape* (counts and bound/unbound/ambiguous mix), **not** the exact step wording. The pin is a
  **structural fingerprint** (A3.3), so innocuous text/whitespace edits don't trip it but any change
  to size or match-mix does.
- Must produce **real matches** so the cache-hit definition and step-completion paths exercise
  populated data structures (an all-undefined corpus would under-measure matching cost).

### A3.2 Shape — commit the artifact, don't regenerate-to-verify

```
tests/Performance/Corpus/
  corpus.manifest.json        // structural fingerprint (A3.3) — the pin
  Bindings/                   // C# step definitions → built to one assembly (reflection path)  ← committed
  Features/                   // .feature files spanning the size envelope                       ← committed
  reqnroll.json                                                                                  ← committed
  Generator/                  // re-pin tool (run by hand when the corpus is intentionally bumped)
```

The **generated files are committed to the repo**. The `corpus.manifest.json` records a **structural
fingerprint** (A3.3) of the checked-in tree, and the drift test re-derives that fingerprint and
compares — it is **not** a regeneration check and **not** a byte hash. "Pinned" means "checked in",
and "unchanged" means "same size and shape".

#### A3.2.1 Why not "regenerate on every run and assert the hash matches"

> An earlier draft proposed pinning by regenerating from a fixed seed and asserting the tree hash.
> **Examining the existing generator showed that assumption is unsafe.**

[`SampleProjectGenerator.Core`](../tests/Reqnroll.SampleProjectGenerator.Core) drives all of its text,
**counts, and structure** from a single shared static PRNG
([`LoremIpsum.Rnd = new(2009)`](../tests/Reqnroll.SampleProjectGenerator.Core/LoremIpsum.cs#L21)) —
~30 draw sites in
[`ReqnrollAssetGenerator`](../tests/Reqnroll.SampleProjectGenerator.Core/ReqnrollAssetGenerator.cs).
It is seeded, so it is pseudo-deterministic *in principle*, but byte-identical regeneration holds only
under fragile conditions:

- **One shared, static, mutable PRNG, never reset per run.** `Rnd` is seeded once at static-ctor time.
  A second generation **in the same process** continues the stream from where the first stopped →
  different bytes. Reproducibility requires a single generation in a fresh process.
- **Data-dependent draw counts.** The number of `Rnd.Next()` draws consumed is itself a function of
  prior draws — `GetUniqueWords` redraws on collisions, `Randomize` does `len·3` swaps, and several
  loop bounds are `Rnd.Next(n)`. Any logic/order/size change shifts the *entire* downstream stream.
- **`public static` + mutable** → not parallel-safe; any other consumer touching `LoremIpsum.Rnd`
  perturbs the sequence.
- (Soft) relies on `System.Random`'s seeded-ctor algorithm staying stable across runtime versions.

Committing the artifact sidesteps all of this: the corpus is reproducible because it is **stored**,
not because it can be re-derived. Regeneration is an explicit, reviewed "bump the pin" operation
(regenerate → review diff → update the manifest hash → commit), run in isolation where the PRNG
fragility doesn't matter.

> If we later *want* trustworthy on-demand regeneration (e.g. to parametrise corpus size), the
> generator must first be made robustly deterministic: thread a **fresh `Random(seed)` instance**
> explicitly through the generation call (no shared static), run single-threaded, and treat the seed
> as part of the pin. That is a generator refactor, tracked separately — not a precondition for T2.

### A3.2.2 Construction options

| Option | How | Pros | Cons |
|---|---|---|---|
| **A. Generate-once, then commit (recommended)** | Run the (seeded) generator once to emit N features + M bindings, review, commit the files; manifest hash guards the committed tree | Exact envelope; no per-run PRNG dependency; deterministic *because stored* | Synthetic step text; larger repo footprint (the corpus is in the tree) |
| B. Curated real corpus | Hand-pick / import real feature files + bindings, commit | Realistic distribution | Hard to hit exact sizes; licensing; also committed, so same footprint cost |

**Recommendation: Option A.** Both options commit the artifact; A just uses the existing generator to
produce it cheaply at the right size.

### A3.3 Structural fingerprint (the pin) and drift detection

The pin is a **structural fingerprint** — the size and shape of the corpus — not its bytes. It is
stored in `corpus.manifest.json` and re-derived by the drift test from the committed corpus. Every
metric is computed deterministically from the **same machinery the server uses** (Gherkin parse +
binding discovery + matcher), so there is no PRNG in the *verification* path even though there was one
in *generation*.

| Metric | Meaning / §9 relevance | How it's computed from the committed corpus |
|---|---|---|
| `featureFileCount` | size envelope (≤ 500) | count `.feature` files under `Features/` |
| `scenarioCount`, `scenarioOutlineCount` | shape | parse each file with the Core Gherkin parser; count scenario/outline nodes |
| `stepCount` | shape (total interactive surface) | total steps across all files (= Σ `FeatureBindingMatchSet.Steps.Count`) |
| `stepDefinitionPatternCount` | binding-pattern envelope (≤ 2,000) | `ProjectBindingRegistry.StepDefinitions.Length` after discovery against the built `Bindings/` assembly |
| `boundStepCount` | match-mix (cache-hit definition + clean diagnostics) | Σ `FeatureBindingMatchSet.Defined.Count()` |
| `unboundStepCount` | match-mix (diagnostics push has work) | Σ `FeatureBindingMatchSet.Undefined.Count()` |
| `ambiguousStepCount` | match-mix (ambiguity path) | Σ `FeatureBindingMatchSet.Ambiguous.Count()` |

These reuse exactly the types already in the codebase:
[`ProjectBindingRegistry.StepDefinitions`](../src/LSP/Reqnroll.IdeSupport.LSP.Core/Bindings/ProjectBindingRegistry.cs)
for the pattern count, and
[`FeatureBindingMatchSet`](../src/LSP/Reqnroll.IdeSupport.LSP.Core/Matching/FeatureBindingMatchSet.cs)'s
`Defined`/`Undefined`/`Ambiguous` partitions for the mix — the same discovery path
[`FixtureDiscovery`](../tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Support/FixtureDiscovery.cs)
already demonstrates.

**Why this is the right pin (not a byte SHA-256).** A byte hash is brittle to the things you don't
care about (step wording, whitespace, line endings) and tells you nothing about the things you do
(did a step flip bound→unbound? did a file get dropped?). The structural fingerprint:

- **Tolerates** step-text edits that preserve matching — the corpus's *value* (its size/shape) is
  unchanged, so the benchmark is still comparable. ✅ passes.
- **Catches** any change to size or shape: a feature/scenario/step added or removed, a binding pattern
  added or removed, or a text edit that changes a step's match status (bound ⇄ unbound ⇄ ambiguous).
  ❌ fails.

This is precisely the invariant that matters for benchmark validity: the §9 workload is defined by
*counts* ("≤500 feature files, ≤2,000 binding patterns"), not by prose.

**Tolerance.** Because the corpus is frozen (committed), the drift test asserts **exact equality** on
every count — simplest, and any deviation is a real edit worth a human looking at. (If a future
parametrised/regenerated corpus is ever adopted, these same metrics can instead be asserted within a
±% band; the fingerprint format doesn't change.)

**Match-mix is deliberate.** The generator/curation is steered so a known share of steps resolves to
exactly one binding (cache-hit definition + clean diagnostics), a share is undefined (diagnostics push
has work to do), and a small share is ambiguous — so the corpus exercises each
[match path](binding-match-service-plan.md) rather than a degenerate all-green or all-red set. The
fingerprint records the resulting counts, so this intent is also what the drift test enforces.

---

# Part B — Layer 4: field instrumentation (T3)

## B1. Goal and the routing wrinkle

Wrap the protocol handlers so each records its own duration and emits it via the **logging path**
(primary) and **optionally a sampled telemetry metric** (secondary). The honest difficulty, from §1:
the four interactive targets live on **three different rails** —

- `semanticTokens/full` → **manual `OnRequest` delegate**
- `completion`, `definition` → **OmniSharp `AddHandler<T>`**
- `publishDiagnostics` → **server push from a MediatR `INotificationHandler`**

A single MediatR `IPipelineBehavior` does **not** cover all three (the manual delegates and the
OmniSharp request pipeline don't flow through our MediatR request pipeline). So the instrumentation is
a small shared **recorder** invoked at each rail's boundary, with a rail-appropriate adapter.

## B2. The recorder

New `Reqnroll.IdeSupport.LSP.Server/Diagnostics/Performance/`:

```csharp
public interface IOperationDurationRecorder
{
    IDisposable Measure(string operation, DocumentUri? uri = null);
    void Record(string operation, double elapsedMs, DocumentUri? uri = null);
}
```

- `Measure` returns a struct/`IDisposable` timing scope (`using var _ = recorder.Measure("textDocument/completion", uri);`).
- Default implementation writes a **structured log line** through `IDeveroomLogger` — e.g.
  `PERF op=textDocument/completion ms=42 bucket=≤50`. Single grep-able prefix so log scraping (or a
  future log-shipping step) can compute field P95 without code changes.
- **Optional sampled telemetry:** when enabled, emit a `PerfSample` event via `ILspTelemetryService`
  at a low sample rate (e.g. 1–5 %) to bound event volume. Following the existing telemetry pattern,
  the dependency is an **optional `ILspTelemetryService? = null`** so unit tests and telemetry-off
  clients are a no-op.
- Registered as a singleton in
  [`ServiceCollectionExtensions`](../src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/ServiceCollectionExtensions.cs),
  alongside the existing telemetry service.

## B3. Wiring per rail

| Rail | Where | How |
|---|---|---|
| Manual `OnRequest` delegates | `InitializeCustomProtocolRouting` | A `Measured(...)` wrapper extension around the delegate registration so `semanticTokens`, references, go-to-step, rename, code lens are all timed at one site — no per-handler edits |
| OmniSharp `AddHandler<T>` (`completion`, `definition`) | The handler bodies | Wrap the handler body in `using recorder.Measure(...)`. Confirmed feasible: these handlers already take injected services (e.g. `SemanticTokensHandler` takes `IDeveroomLogger`); add the recorder the same way. Two handlers, two small edits |
| Server push (`publishDiagnostics`) | `DiagnosticsPublishHandler.Handle` | Record `MatchCacheChangedNotification`-arrival → push-sent here. This is the one place that can see the push timing the §9 target is phrased against |

**Why not force everything onto MediatR first?** Re-routing the manual delegates and OmniSharp
handlers through a uniform MediatR pipeline purely to instrument them is a large, risky refactor with
no feature payoff and known OmniSharp registration constraints
([[lsp-handler-dynamic-registration]]). The wrapper-at-the-boundary approach is small, local, and
covers the four targets. Revisit a uniform pipeline only if instrumentation coverage needs to expand
broadly.

## B4. Privacy (telemetry path only)

The logging path is local and may include the URI. The **telemetry** path must obey §9 Telemetry &
Privacy:

- **No absolute paths / project names / file content.** Send the **operation name** and a **duration
  bucket** (or raw ms), never the URI. If a file dimension is ever needed, use a one-way hash, not the
  path — consistent with the `OpenProject`/`Error` scrubbing rules.
- **Opt-out respected** — telemetry emission already flows through the opt-out gate on the VS host
  (`IEnableAnalyticsChecker`); the recorder only emits the notification, it does not bypass consent.
- **Add `PerfSample` to the public telemetry inventory** (§9 commits to publishing one) and to
  [`build-plan-telemetry-capture.md`](build-plan-telemetry-capture.md), with the `IDEClient`
  dimension so field P95 can be sliced per IDE — the same breakdown the data-model enhancements table
  already calls for.

---

## 2. Testing & verification of the verifiers

- **Layer 2 harness self-test:** an `xUnit` smoke test (in the existing
  `LSP.Server.Specs` or a thin test project) that runs the driver against a **tiny** corpus subset
  with low iteration counts, asserting it produces a populated `OperationResult` for every operation —
  guards against the harness silently measuring nothing (e.g. a handler returning `null` for non-corpus
  URIs, as `SemanticTokensHandler` does for non-`.feature` files).
- **Corpus drift test (the pin):** re-derive the structural fingerprint (A3.3) from the **committed**
  corpus — parse the features, discover the bindings, run the matcher — and assert every count equals
  `corpus.manifest.json`. Fails if the checked-in corpus changes size or shape (incl. a step flipping
  bound⇄unbound⇄ambiguous); ignores step-text/whitespace edits that don't move a count. It does **not**
  regenerate the corpus (regeneration is not byte-stable — see A3.2.1) and does **not** hash bytes.
  Reuses the `FixtureDiscovery` discovery path and `FeatureBindingMatchSet` partitions.
- **Recorder unit tests:** `Measure` logs the structured line with elapsed ms; with a null telemetry
  service it does not throw and emits no event; sampling honours the configured rate.
- **No assertion in `dotnet test` for absolute thresholds.** The benchmark console is **not** part of
  the normal test sweep; only the reference machine asserts. (Per
  [[core-tests-avoid-stubidescope]] keep the new tests building inputs directly — the benchmark host
  builds the real server, not a `VsxStubs` scope.)

## 3. Phasing

| Phase | Deliverable | Gate |
|---|---|---|
| 1 | Generated-and-committed corpus + `corpus.manifest.json` (T2) | Drift test (structural fingerprint of committed tree) green; sizes within §9 envelope |
| 2 | `BenchmarkLspHarness` + interactive scenarios (T1) | Driver reports P50/P95/P99 for the 5 interactive ops |
| 3 | Batch scenarios (cold start, Roslyn, reflection) (T1) | All 8 §9 ops measured; JSON output |
| 4 | Reference-machine gating + baseline JSON | `--assert` exits non-zero on breach; one baseline captured on the reference box |
| 5 | `IOperationDurationRecorder` + log path (T3) | `PERF` lines appear for the four interactive targets in a live run |
| 6 | Sampled telemetry `PerfSample` + inventory entry (T3) | Event visible end-to-end; privacy review; opt-out honoured |

Phases 1–4 (Layer 2) and 5–6 (Layer 4) are independent and can proceed in parallel.

## 4. Open questions

- **Reference machine identity.** Which box is "designated"? Its spec must be recorded next to the
  baseline JSON, since absolute numbers are only meaningful against known hardware.
- **Debounce source for the diagnostics target.** Confirm whether there is an explicit debounce
  window today; if not, the `didChange → push` measurement is the quantity and the "minus debounce"
  reporting is unnecessary.
- **`telemetry/event` vs. direct HTTP for `PerfSample`.** Inherits the unresolved telemetry-origin
  question ([Q11](LSP-IDE-Support-Open-Questions.md)); `PerfSample` rides whatever transport that
  resolves to and needs no independent decision.
- **Layer 3 adoption trigger.** The JSON baseline format is built here; deciding *when* to turn on
  per-PR regression gating (and on which runner) is deferred but now cheap.
```
