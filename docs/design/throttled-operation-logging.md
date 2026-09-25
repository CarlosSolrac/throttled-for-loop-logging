# ThrottledLogging — API design

Status: **implemented, v0.5** · 2026-09-22 · 62 tests green on net8.0 and net10.0

Where the built library differs from the design as written, §12 says so and this document has been
corrected to describe what exists.

## 1. What this library is for

Instrument a long-running function — with or without an inner loop — through `ILogger`
dependency injection, so that you get entry/exit visibility and per-item progress
**without emitting one log line per iteration**.

The shape of the problem:

```
ImportOrders() starts                      -> 1 line
  for each of 250,000 orders
      "processing order X" starts          -> 250,000 lines
      succeeded / failed                   -> 250,000 lines
ImportOrders() succeeds                    -> 1 line
```

Half a million lines for one job. What an operator actually wants is: it started, it is
on item 173,402 of 250,000, 41 have failed, it is moving at 900/s, it should finish in
about 90 seconds, and here is the most recent thing it was working on.

## 2. Requirements

Captured from the thread, numbered so the tests can cite them.

| # | Requirement |
|---|---|
| R1 | Log entry to a function, then success or failure on exit. |
| R2 | Inside a loop, log the start of processing an item, then success or failure of that item. |
| R3 | Drive everything through `ILogger` dependency injection. |
| R4 | Always log the **first** event inside the loop. |
| R5 | After the first, **save the last event submitted** instead of emitting it. |
| R6 | When a **count threshold** or a **time threshold** is reached, log the saved (latest) event. |
| R7 | The emitted record says whether the event is **new since the last logged event**. |
| R8 | The emitted record carries the **UTC time the event was submitted**. |
| R9 | Thread safe. |
| R10 | A method that reports the internal counters. |
| R11 | Keep a running aggregate of per-iteration durations and provide an ETA range. |
| R12 | A method, callable concurrently, listing every active operation with processed / pending / failed / ETA. |
| R13 | Failures must not skew the ETA. *Revised §6.7: the fix is to split predictive from descriptive statistics, not to exclude failures by outcome.* |
| R14 | After the first failure, failures are throttled too — a run can produce 250,000 of them. |

Two things follow from R5 that are worth stating plainly, because they set this library
apart from an ordinary log deduplicator:

- Suppressed events are **not discarded and counted** — the newest one is **held and later
  emitted**. What you lose is the middle of the burst, not the end of it.
- Because the held event is emitted late, the record must carry its **own** submit time
  (R8), not the time it reached the log. Otherwise every timestamp in the log is a lie.

## 3. Public surface

Three concepts: a **factory** you inject, an **operation scope** for the function, and an
**item scope** for one turn of the loop. Plus a **registry** for out-of-band inspection.

```csharp
namespace ThrottledLogging;

/// <summary>Creates operation scopes. Registered as a singleton.</summary>
public interface IOperationLogger
{
    IOperationScope BeginOperation(string name, OperationOptions? options = null);
}

/// <summary>One long-running function. Disposing it ends the operation.</summary>
public interface IOperationScope : IDisposable
{
    Guid Id { get; }
    string Name { get; }

    /// <summary>Begins one turn of the loop. Disposing it ends that item.</summary>
    IItemScope BeginItem(string? label = null);

    void Success(string? note = null);
    void Failure(Exception error);

    /// <summary>Point-in-time view of this operation. Safe from any thread.</summary>
    OperationSnapshot Snapshot();
}

/// <summary>One iteration. Timing starts at creation and stops at Success/Failure/Dispose.</summary>
public interface IItemScope : IDisposable
{
    void Success();
    void Failure(Exception error);
}
```

### 3.1 Usage — the explicit form

```csharp
public sealed class OrderImporter(ILogger<OrderImporter> logger, IOperationLogger operations)
{
    public async Task ImportAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        using IOperationScope op = operations.BeginOperation("ImportOrders", new OperationOptions
        {
            TotalItems    = orders.Count,               // enables the ETA
            EveryItems    = 500,                        // count threshold   (R6)
            EveryInterval = TimeSpan.FromSeconds(10),   // time threshold    (R6)
        });

        foreach (Order order in orders)
        {
            using IItemScope item = op.BeginItem(order.Id);
            try
            {
                await ProcessAsync(order, ct);
                item.Success();
            }
            catch (OrderException ex)
            {
                item.Failure(ex);                       // failures are never throttled
            }
        }

        op.Success();
    }
}
```

### 3.2 Usage — the delegate form

`using` alone cannot tell "returned normally" from "threw", so the explicit form needs you
to remember `op.Success()`. The delegate overloads remove that footgun and are the
recommended default:

```csharp
await operations.RunAsync("ImportOrders", async op =>
{
    await op.ForEachAsync(orders, static async (order, ct) => await ProcessAsync(order, ct), ct);
},
new OperationOptions { TotalItems = orders.Count }, ct);
```

`ForEachAsync` takes an optional `MaxDegreeOfParallelism`, times each item, and routes
exceptions to `item.Failure` for you.

Disposal without a recorded outcome is treated differently at the two levels, on purpose:

- an **item** counts as **failed**, because the usual way it happens is an exception unwinding
  past the `using`, and calling that a success would hide exactly the items worth seeing. It
  appears in the failure breakdown under `(unrecorded)`;
- an **operation** is logged as **Incomplete**, a state of its own, because at that level there is
  no useful default and guessing would be worse than saying so.

## 4. Throttling behaviour (R4–R8, R14)

Per operation the library keeps:

| Field | Purpose |
|---|---|
| `_submitted` | total events offered since the operation began |
| `_sinceLastLog` | events offered since the last emission — drives the count threshold |
| `_lastLogAtUtc` | drives the time threshold |
| `_pending` | the **latest** submitted event, held for later emission (R5) |
| `_lastLoggedSignature` | what was last emitted, so `IsNew` can be computed (R7) |

The decision on each submitted event:

```
first event for this operation?            -> emit now                       (R4)
event is a failure?                        -> emit now  (configurable)
_sinceLastLog + 1 >= EveryItems?           -> emit now, it becomes the latest (R6)
now - _lastLogAtUtc >= EveryInterval?      -> emit now                        (R6)
otherwise                                  -> store as _pending, return       (R5)
```

When a threshold fires, what is emitted is `_pending` (the newest held event), not the
event that happened to trip the threshold — those are the same thing when the trip happens
on submit, and differ when the background sweeper fires.

### 4.1 The emitted record

```csharp
public readonly record struct ThrottledEvent
{
    public required string        OperationName   { get; init; }
    public required Guid          OperationId     { get; init; }
    public          string?       ItemLabel       { get; init; }
    public required ItemOutcome   Outcome         { get; init; }   // Started | Succeeded | Failed
    public required DateTimeOffset SubmittedAtUtc { get; init; }   // R8 — when it happened
    public required DateTimeOffset EmittedAtUtc   { get; init; }   //      when it reached the log
    public required bool          IsNew           { get; init; }   // R7
    public required long          SuppressedSince { get; init; }   // events held back since the last emission
    public required OperationSnapshot Progress    { get; init; }   // counts + ETA
}
```

Rendered, one line looks like:

```
info: ImportOrders[1001] Processing order-173402 — 173,402/250,000 done, 41 failed,
      new=true, submitted 2026-09-22T03:14:09.221Z (2.1s ago), 1,847 held since last line,
      900/s, ETA 00:01:25 (00:01:12–00:01:41)
```

Every field is also on the structured state, so it is queryable:
`operation.name`, `operation.id`, `item.label`, `item.outcome`, `event.submitted_at`,
`event.is_new`, `event.suppressed_since`, `progress.processed`, `progress.failed`,
`progress.pending`, `progress.rate_per_second`, `progress.eta_seconds`,
`progress.eta_low_seconds`, `progress.eta_high_seconds`.

### 4.2 `IsNew` — what "new" is compared against

**Resolved 2026-09-22.** `IsNew` answers *has anything arrived since the last line I
emitted?* — the stalled-loop signal. It is implemented with a **monotonic sequence number**,
not a GUID and not a content signature.

Every submitted event gets `Interlocked.Increment(ref _sequence)`, carried **on** the
immutable pending record so it is published atomically with the event by the same
`Interlocked.Exchange`. A sequence stored in a separate field could tear relative to the
event you read. On emission:

```csharp
bool isNew         = pending.Sequence != _lastEmittedSequence;
long suppressedSince = pending.Sequence - _lastEmittedSequence - 1;   // free, exact
_lastEmittedSequence = pending.Sequence;
```

- At least one event submitted since the last emission -> sequence differs -> `IsNew` true.
- Nothing submitted since (the sweeper firing twice on the same held event) -> sequence
  unchanged -> `IsNew` false. The loop is stalled, which is the signal worth having.

#### Why a sequence rather than a GUID

A fresh GUID per submitted event gives *exactly the same semantics* — the comparison is
against the last **emitted** marker, so a repeated sweeper flush on an unchanged held event
correctly reports `false`. The design is sound; it is only the mechanism that is expensive.
Measured on the build machine (.NET 10.0.12, x64, 50M iterations, warmed; a shared VM, so
read the ratios rather than the absolutes):

| Candidate | ns/op | vs sequence |
|---|---:|---:|
| `Guid.NewGuid()` (v4) | 367.65 | 43.0x |
| `Guid.CreateVersion7()` | 393.29 | 46.0x |
| `Stopwatch.GetTimestamp()` | 21.93 | 2.6x |
| `HashCode.Combine(label, outcome)` | 15.77 | 1.8x |
| `string.GetHashCode()` (12 chars) | 10.11 | 1.2x |
| **`Interlocked.Increment(ref long)`** | **8.54** | **1.0x** |

A v4 GUID is 128 bits of cryptographic randomness, and the entropy is what costs. None of
it is needed to compare an event against the previous one inside a single operation. The
suppressed path is meant to be one increment, one reference exchange and two comparisons;
adding a GUID would make it cost more than everything else in it combined. The sequence is
also half the width and hands you `SuppressedSince` for free from the gap, which a GUID
cannot give you at any price.

GUIDs stay where global uniqueness is genuinely worth 368 ns: `OperationScope.Id`, paid once
per long-running function, for correlating lines across logs and processes.

#### What a sequence cannot tell you

Neither a sequence nor a GUID detects **semantic repetition**. A retry loop hammering one
failing order submits distinct events forever, so `IsNew` is true every time and nothing
says you are stuck on the same item. If that case matters, it needs a content signature,
and a signature is cheap — `HashCode.Combine(label, outcome)` is 1.8x the increment and can
be computed lazily only when `ItemLabel` is non-null:

```csharp
public required bool IsNew      { get; init; }   // sequence: anything arrived at all?
public          bool? IsSameItem { get; init; }  // signature: same item as last time?  (null when no label)
```

**Proposed:** ship `IsNew` on the sequence now, and add `IsSameItem` behind the optional
signature only if the retry-loop case turns out to matter in practice.

### 4.3 The time threshold when nothing is submitted

Evaluating the time threshold only on submit means a loop that stalls on one slow item
goes silent — precisely when you want a heartbeat. So an optional background sweeper
(`PeriodicTimer`, one per `IOperationLogger`, not per operation) walks the active
operations and flushes any whose `EveryInterval` has elapsed.

**Agreed 2026-09-22: sweeper on by default**, tick = `min(EveryInterval) / 4`, clamped to
[1s, 30s]. It costs one timer for the whole process.

### 4.4 Failures are a separate channel (R14)

A run that fails 250,000 times must not emit 250,000 lines, and it must not let the failure
line be crowded out by progress either. So each operation carries **two independent
throttle channels**, each with its own pending slot, sequence and thresholds:

| Channel | Carries | Default thresholds |
|---|---|---|
| **Progress** | `Started`, `Succeeded` | every 500 items or 10s |
| **Failure** | `Failed` | every 50 failures or 5s |

Independent state is the point. Sharing one slot would mean a flood of successes buries the
one failure you needed to see, and a flood of failures buries all progress. Separate slots
also let the first failure emit immediately (R4 applied per channel) even when the progress
channel is mid-window.

Failures are rarer and more interesting than successes, so their thresholds default tighter.

`Started` and `Succeeded` share the progress slot deliberately: the slot holds the *latest*
event, so a loop stuck on one item leaves that item's `Started` sitting in the slot for the
sweeper to flush, which is exactly the signal wanted. Splitting them into three channels
would triple the volume at each flush for no extra information.

#### Failure breakdown in the final summary

Throttling failures means most of them are never individually logged, so the end-of-operation
summary has to account for them in aggregate. Each operation keeps a bounded count by
exception type — a `ConcurrentDictionary<string, long>` keyed on the exception type name,
capped at 20 distinct types with the remainder bucketed as `(other)` so a pathological run
cannot grow it without limit:

```
warn: ImportOrders finished with failures — 249,983 of 250,000 failed in 00:41:12
      TimeoutException 248,404 · SqlException 982 · HttpRequestException 591 · (other) 6
      last failure: order-249998 at 2026-09-22T04:31:07.882Z
```

The most recent held failure is always flushed as part of ending the operation, so a run
that ends badly never hides its last error.

## 5. Thread safety (R9)

| State | Mechanism |
|---|---|
| Counters (`_submitted`, `_processed`, `_failed`, `_sinceLastLog`) | `Interlocked.Increment` / `Interlocked.Add` on `long` |
| `_pending` | immutable record class, published with `Interlocked.Exchange` — last writer wins, which *is* the R5 semantic |
| Emission | `Interlocked.CompareExchange` on a flush gate, so exactly one thread emits and the rest return immediately |
| Duration statistics | a short `lock` around the moment accumulators (see §6.6) |
| Active operations | `ConcurrentDictionary<Guid, OperationState>`, entry removed on dispose |
| Failure breakdown | `ConcurrentDictionary<string, long>` keyed on exception type, bounded at 20 |
| Both channels | identical mechanism, entirely separate fields — no shared state, so no cross-channel contention |

The hot path for a **suppressed** event is one increment, one reference exchange and two
comparisons — no allocation, no lock, and critically **no message formatting**, because the
formatter is never invoked for an event that is not emitted. That is where the saving is.

The single `lock` is on the statistics update only. It is held for a few nanoseconds and is
uncontended relative to the work being measured; a lock-free version using CAS loops on
`double` is possible but not worth the complexity until a benchmark says otherwise.

## 6. ETA (R11, R13) — and a recommendation

You asked whether there is a better way than arithmetic mean plus geometric mean. Short
answer: **the arithmetic mean is the right centre, the geometric mean is the wrong bound,
and the biggest accuracy win is not the choice of mean at all.**

Notation: `n` items completed, `N` total, `R = N − n` remaining, durations `x₁…xₙ`.

### 6.1 Why AM/GM does not give you a range

- By the AM–GM inequality the geometric mean `g` is **always ≤** the arithmetic mean `μ`,
  so `[R·g, R·μ]` is always a correctly ordered interval. That is the only guarantee it has.
- Its **width is driven by the spread of item durations**, not by the uncertainty of the
  answer. Uniform items give a hairline band that looks confident and is not; heavy-tailed
  items give an absurdly wide low side.
- Most importantly, an ETA is a statement about a **sum**. By linearity of expectation
  `E[Σ remaining] = R·μ`. The geometric mean estimates a *typical single* item, not a sum,
  so `R·g` is a systematic underestimate rather than a plausible best case. It will read as
  optimistic every single time, and operators will learn to ignore the low number.

### 6.2 A band that means something

The remaining work is a sum of `R` draws, so its variance is `R·σ²` and:

```
ETA        = R · μ
ETA range  = R · μ  ±  z · σ · √R           z = 1.28 (80%)  or  1.96 (95%)
```

Note the `√R`: the band tightens as the loop progresses, which is the behaviour you want
and which an AM/GM band does not have. Cost is `count`, `mean`, `M2` via Welford — O(1)
memory, streaming, no history retained.

### 6.3 The bigger win: track drift

Real loops are not stationary. Caches warm, connection pools saturate, the database gets
busier, GC pressure builds. An all-time mean is anchored to ancient history and this is
usually a **far larger error source than the choice of mean**. Use exponentially weighted
statistics instead:

```
α  = 1 − 2^(−1/h)                 h = half-life in items, default 50
μ  ← μ + α·(x − μ)
σ² ← (1 − α)·(σ² + α·(x − μ_prev)²)
```

Same O(1) cost, follows change.

### 6.4 Parallelism: estimate from throughput, not from item duration

This matters for your three-threads case. With `P` workers running concurrently, `R·μ`
overestimates by roughly a factor of `P`, because wall-clock time and summed item time are
no longer the same thing. Estimating from **observed completion rate** is correct by
construction and needs no knowledge of `P`:

```
rate = EWMA of completions per second        (half-life ~30s)
ETA  = R / rate
```

It degrades exactly to `R·μ` when `P = 1`, and it automatically absorbs per-item overhead,
scheduling gaps and thread-pool starvation that per-item timing never sees.

### 6.5 Recommendation — **agreed 2026-09-22**

1. **Primary ETA: `R / rate`**, with `rate` an EWMA of throughput. Parallelism-correct and
   drift-tracking.
2. **Band: `±z·σ·√R / P`**, derived from the EWMA variance, reported with the confidence
   level stated rather than as a bare "min/max".
3. **Keep the geometric mean — move it.** Report it as *typical item duration* in the
   statistics block, where its outlier resistance is a genuine advantage over the
   arithmetic mean. Just do not build an ETA bound out of it.
4. **Publish the inputs too** (`processed`, `total`, `elapsed`, `rate`) so a reader can
   sanity-check the ETA instead of trusting it.
5. **Guard rails:** no ETA until `n ≥ 5` and `elapsed ≥ 1s`; `null` ETA when `TotalItems`
   is unknown; clamp to `≥ 0`; round to whole seconds so it does not look falsely precise.
6. **Optional, if your item durations turn out bimodal** (cache hit vs miss, small vs large
   payload): add a P² quantile sketch (O(1) memory) and report `R·p50 … R·p90` as a
   descriptive range. Worth adding only if the data warrants it.

If you would rather keep your AM/GM range as well, it costs nothing once the running
log-sum is accumulated — I would expose it as an extra field rather than as the headline.

### 6.6 What gets accumulated

Per operation, all O(1). Which items feed which accumulator is set out in §6.7:

```
_count          long      completed items (success or failure)
_sumTicks       long      Σ duration            -> arithmetic mean
_sumLogTicks    double    Σ ln(duration)        -> geometric mean  (floor at 1 tick so ln is defined)
_ewmaTicks      double    drift-tracking mean
_ewmaVarTicks   double    drift-tracking variance
_ewmaRate       double    completions per second
_minTicks/_maxTicks long

_completions    long      successes + failures -- the throughput numerator (see 6.7)
```

### 6.7 Failures and the ETA (R13) — **revised 2026-09-22**

Carlos asked: if a failure takes 60 seconds just like a success, should it not count toward
the ETA? Following that through changes the answer from v0.3, and simplifies it.

#### The general rule

**Any outcome-based exclusion biases the ETA.** The direction just depends on whether
failures are slower or faster than successes:

| Scenario (250,000 items) | True mean/item | Successes-only mean | Error |
|---|---:|---:|---:|
| 80% succeed @ 100ms, 20% **time out @ 60s** | 12.08 s | 0.1 s | **121× too fast** (35 days predicted as 7 hours) |
| 80% succeed @ 100ms, 20% **fail fast @ 1ms** | 80.2 ms | 100 ms | **25% too slow** |

An ETA is a prediction of wall-clock. Every item that completes consumed real wall-clock,
whatever its outcome, so every item is evidence about what the remaining ones will cost.
Dropping a subset makes the estimate wrong by however much that subset differed — which is
precisely the same argument already made for the throughput denominator. v0.3 applied it to
the denominator and not to the durations; that was inconsistent.

#### The resulting design

Split the statistics by **purpose**, not by outcome:

```
// Predictive -- feeds the ETA. Every completed item, success or failure.
_ewmaRate       EWMA of completions per second
_ewmaTicks      EWMA of item duration          <- failures now included
_ewmaVarTicks   EWMA variance                  <- failures now included

// Descriptive -- reported for humans, never feeds the ETA. Successes only.
_sumTicks       arithmetic mean of a successful item
_sumLogTicks    geometric mean, the "typical successful item"
_failSumTicks   mean of a failed item, reported alongside so the mix is visible
```

`MeanItemDuration` and `TypicalItemDuration` stay successes-only, because "how long does a
successful item take" is the question a human is actually asking. They are reported, not
used. `MeanFailureDuration` sits next to them.

#### Why no `X` seconds threshold

An absolute threshold has to be configured per operation against a typical item duration
the caller must already know. Set it at 1s when items take 5ms and it excludes nothing; set
it at 1s when items take 5 minutes and it excludes nothing either. It is a magic number
nobody will tune, and it cannot adapt when the workload shifts mid-run.

Including everything needs no threshold and handles both of Carlos's cases for free:

- success and failure both 60s -> both counted, ETA correct;
- failures fast, successes slow -> both counted in their true proportion, ETA correct;
- the mix changes halfway through -> the EWMA half-life (default 50 items) forgets the old
  mix on its own, which no fixed threshold can do.

#### Where a threshold *does* earn its place

Two narrower uses, both worth having, neither of them an outcome filter:

1. **In-flight items.** An item that has been running for ten minutes and has not finished
   yet contributes nothing to any statistic, so the ETA quietly goes stale-optimistic while
   the loop hangs. Counting open item scopes whose elapsed time already exceeds the current
   mean, and flooring the ETA at that elapsed time, fixes a real blind spot.
2. **Variance clamping.** One pathological ten-minute hang can blow the confidence band
   wide open for the next fifty items, so the deviation fed to the variance is clamped at four
   standard deviations of the current distribution — relative to what has been observed, not an
   absolute second count. The mean still takes the item unclamped, so the estimate follows
   reality while the band stays usable. (The design first called for winsorising at p99; a
   k-sigma clamp achieves the same thing without carrying a quantile sketch, and is what the
   code does.)

**Proposed:** implement both, drop the outcome filter. Flagged rather than assumed, since it
reverses R13 as originally written.

## 7. Counters and live inspection (R10, R12)

```csharp
/// <summary>Process-wide view. Registered as a singleton alongside IOperationLogger.</summary>
public interface IOperationRegistry
{
    /// <summary>Every operation currently in flight. Safe to call from any thread while they run.</summary>
    IReadOnlyList<OperationSnapshot> GetActiveOperations();

    /// <summary>Awaitable convenience over <see cref="GetActiveOperations"/>.</summary>
    ValueTask<IReadOnlyList<OperationSnapshot>> GetActiveOperationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Aggregate throttling counters since process start. (R10)</summary>
    ThrottleCounters GetCounters();
}

// A sealed record rather than a readonly record struct: it carries around twenty members and is
// normally handed out in lists, so copying would cost more than the allocation saves.
public sealed record OperationSnapshot
{
    public required Guid            Id                 { get; init; }
    public required string          Name               { get; init; }
    public required DateTimeOffset  StartedAtUtc       { get; init; }
    public required TimeSpan        Elapsed            { get; init; }

    public required long            Processed          { get; init; }   // succeeded
    public required long            Failed             { get; init; }
    public required long?           Total              { get; init; }   // null when unknown
    public required long?           Pending            { get; init; }   // Total - Processed - Failed
    public required int             InFlight           { get; init; }   // item scopes open right now

    public required double          RatePerSecond      { get; init; }   // completions/s: successes + failures
    public required double          FailureRate        { get; init; }   // 0..1, the mix behind the ETA
    public required TimeSpan?       MeanItemDuration      { get; init; }  // successful items, descriptive
    public required TimeSpan?       TypicalItemDuration   { get; init; }  // geometric mean of successful items
    public required TimeSpan?       MeanFailureDuration   { get; init; }  // so the mix behind the ETA is visible
    public required TimeSpan?       LongestInFlight       { get; init; }  // oldest open item scope (§6.7)
    public required EtaEstimate     Eta                { get; init; }

    public required string?         LastItemLabel      { get; init; }
    public required DateTimeOffset? LastEventSubmittedAtUtc { get; init; }
    public required string?         LastFailureLabel   { get; init; }
    public required DateTimeOffset? LastFailureAtUtc   { get; init; }
    public required IReadOnlyDictionary<string, long> FailuresByType { get; init; }
}

public readonly record struct EtaEstimate
{
    public TimeSpan?       Remaining            { get; init; }   // central
    public TimeSpan?       RemainingLow         { get; init; }
    public TimeSpan?       RemainingHigh        { get; init; }
    public DateTimeOffset? ExpectedCompletionUtc { get; init; }
    public double          Confidence           { get; init; }   // e.g. 0.80
}

// Held-back events are counted when the line that supersedes them is written, so an event still
// being held is in EventsSubmitted but in neither of the other two. The three balance once every
// operation has ended.
public readonly record struct ThrottleCounters
{
    public required long EventsSubmitted     { get; init; }
    public required long EventsLogged        { get; init; }
    public required long EventsHeldBack      { get; init; }
    public required long FlushesByCount      { get; init; }
    public required long FlushesByTime       { get; init; }
    public required long FlushesBySweeper    { get; init; }

    // The failure channel keeps its own tallies (section 4.4).
    public required long FailuresSubmitted   { get; init; }
    public required long FailuresLogged      { get; init; }
    public required long FailuresHeldBack    { get; init; }
    public required int  ActiveOperations    { get; init; }
    public required long CompletedOperations { get; init; }
    public required long FailedOperations    { get; init; }
}
```

Your three-threads example, from a health endpoint or a debug command:

```csharp
IReadOnlyList<OperationSnapshot> active = await registry.GetActiveOperationsAsync(ct);
foreach (OperationSnapshot op in active)
{
    Console.WriteLine($"{op.Name,-20} {op.Processed,8:N0}/{op.Total,-8:N0} " +
                      $"failed {op.Failed,4:N0}  pending {op.Pending,8:N0}  " +
                      $"{op.RatePerSecond,7:N1}/s  ETA {op.Eta.Remaining:hh\\:mm\\:ss}");
}
```

**Agreed 2026-09-22: ship both.** The snapshot does no I/O — it reads `Interlocked`
counters out of a `ConcurrentDictionary` and is safe to call concurrently with running
loops — so the synchronous overload is not a blocking call. The `ValueTask` overload wraps
it so it composes with `await`-based call sites such as a health endpoint.

Three concurrent threads can be modelled either way and both are supported:

- **three separate operations** — each thread calls `BeginOperation`, the registry lists three rows;
- **one operation, three workers** — one `BeginOperation`, `ForEachAsync(..., MaxDegreeOfParallelism: 3)`,
  the registry lists one row whose `InFlight` is 3 and whose throughput-based ETA already
  accounts for the concurrency (§6.4).

## 8. Configuration

```csharp
public sealed class OperationOptions
{
    public long?     TotalItems              { get; init; }

    /// <summary>Progress channel: Started and Succeeded events. (section 4.4)</summary>
    public int       EveryItems              { get; init; } = 500;
    public TimeSpan  EveryInterval           { get; init; } = TimeSpan.FromSeconds(10);
    public LogLevel  Level                   { get; init; } = LogLevel.Information;

    /// <summary>Failure channel: independent slot and thresholds, deliberately tighter. (R14)</summary>
    public int       FailureEveryItems       { get; init; } = 50;
    public TimeSpan  FailureEveryInterval    { get; init; } = TimeSpan.FromSeconds(5);
    public LogLevel  FailureLevel            { get; init; } = LogLevel.Warning;
    public int       MaxTrackedFailureTypes  { get; init; } = 20;

    public int       EtaHalfLifeItems        { get; init; } = 50;
    public double    EtaConfidence           { get; init; } = 0.80;
}
```

Registration:

```csharp
services.AddThrottledLogging(options =>
{
    options.Defaults.EveryItems    = 1_000;
    options.Defaults.EveryInterval = TimeSpan.FromSeconds(15);
    options.EnableSweeper          = true;
});
```

Bindable from `IConfiguration` with `AddThrottledLogging(section)`, and reloaded when it changes:
the logger follows `IOptionsMonitor`, validates each new version whole, and swaps it in for
operations begun afterwards; running operations keep their own copy, and a version that fails
validation is logged (9011) and ignored. `ThrottledLoggingOptions` also carries an `OnEmitted` observer,
called with every event that survives throttling, for pushing the same data to metrics and for
asserting on structure in tests rather than parsing log text.

## 9. Decisions already taken in the scaffold

| Decision | Reasoning | Reversible? |
|---|---|---|
| Library multi-targets `net8.0;net10.0` | `TimeProvider` is BCL from .NET 8, and both are LTS | yes, one line |
| Floors on `Microsoft.Extensions.Logging.Abstractions` 8.0.3 | lowest version that suffices, so consumers are never forced to upgrade their graph | yes |
| xUnit **v3** (4.0.1) on Microsoft.Testing.Platform | current xUnit line; MTP is the .NET 10 SDK default and VSTest is no longer supported there | costly later |
| `FakeLogger` + `FakeTimeProvider` as the test doubles | official Microsoft doubles; `FakeTimeProvider` is what makes the time threshold testable without sleeping | yes |
| `TimeProvider` injected everywhere, never `DateTime.UtcNow` | otherwise the time threshold and the ETA are untestable | no — design-level |
| Warnings as errors, XML docs required on the public surface | matches the house style for this project | yes |

## 10. Decisions

All questions raised in v0.1 are now settled.

| # | Question | Decision |
|---|---|---|
| 1 | `IsNew` comparison basis | Monotonic `Interlocked` sequence, not a GUID and not a signature (§4.2). A signature-based `IsSameItem` is deferred. |
| 2 | ETA presentation | Throughput-based EWMA central value with a stated-confidence band; geometric mean demoted to "typical item duration" (§6.5). |
| 3 | Background sweeper on by default | Yes — one timer per process, so a stalled loop still heartbeats (§4.3). |
| 4 | Sync or async snapshot | Both (§7). |
| 5 | Does `BeginItem` emit a "Started" event | Yes. It shares the progress slot with `Succeeded` (§4.4). |
| 6 | Failures in the ETA | **Revised.** Every completed item feeds the predictive statistics, whatever its outcome; successes-only statistics are kept but reported rather than used. No `X`-second threshold — an outlier clamp and an in-flight floor instead (§6.7). |
| 7 | Failure throttling | Independent second channel with its own slot, sequence and tighter thresholds; bounded breakdown by exception type in the final summary (§4.4). |

### Still open

Only one, and it does not block implementation:

- **Is the retry-loop case real for you?** If a loop can hammer the same item repeatedly,
  `IsNew` will read true every time and never reveal that you are stuck on one item. The
  fix is the deferred `IsSameItem` signature flag (§4.2), which costs 1.8x an increment and
  only when a label is present.

  `samples/ThrottledLogging.RetrySample` now demonstrates this rather than describing it.
  Its third pass retries one order three hundred times and every emitted line reads
  `new=True`, which is the same shape three hundred *different* orders would produce; the
  same order hanging inside a single attempt produces `new=False` sweeper heartbeats and is
  obviously stuck. Both loops are equally stuck and only one looks it. The sample also shows
  the choice that comes first: modelling an attempt as an item breaks `Processed`, `Pending`
  and the ETA, while modelling the whole retry sequence as one item keeps them honest.

## 11. Tests

These were the red tests written first, in order. The suite has since grown to **65**, adding
channel-level tests for the state machine, parallel-submission tests, dependency-injection wiring
and options validation.

1. `BeginOperation_logs_entry_once`  (R1)
2. `Operation_logs_success_on_Success`  (R1)
3. `Operation_logs_failure_with_exception_on_Failure`  (R1)
4. `First_item_in_loop_is_always_logged`  (R4)
5. `Second_item_is_held_and_not_logged`  (R5)
6. `Held_event_is_logged_once_count_threshold_is_reached`  (R6)
7. `Held_event_is_logged_once_time_threshold_elapses`  (R6, `FakeTimeProvider`)
8. `Logged_event_carries_the_submit_time_not_the_emit_time`  (R8)
9. `IsNew_is_false_when_the_held_event_matches_the_last_logged_event`  (R7)
10. `Counters_report_submitted_logged_and_held_back`  (R10)
11. `Concurrent_submitters_never_lose_a_count`  (R9, N threads × M items, assert exact totals)
12. `Concurrent_submitters_emit_each_flush_exactly_once`  (R9)
13. `Eta_is_null_before_the_minimum_sample_size`  (R11)
14. `Eta_tracks_a_step_change_in_item_duration`  (R11, EWMA behaviour)
15. `GetActiveOperations_lists_every_in_flight_operation`  (R12)
16. `Failed_items_do_not_change_the_reported_mean_item_duration`  (R13 — descriptive stays successes-only)
17. `Failed_items_do_count_toward_the_throughput_rate`  (R13 — guards the §6.7 bias)
17a. `Slow_failures_lengthen_the_eta`  (R13 — 80% @ 100ms, 20% @ 60s; assert the ETA is near the true mean, not 121x fast)
17b. `Fast_failures_shorten_the_eta`  (R13 — the mirror case, assert no 25% overestimate)
17c. `Eta_is_floored_by_the_longest_in_flight_item`  (§6.7 — a hung item must not leave the ETA stale-optimistic)
17d. `A_single_pathological_duration_does_not_blow_open_the_confidence_band`  (§6.7 winsorising)
18. `First_failure_is_always_logged_immediately`  (R14, R4 per channel)
19. `Second_failure_is_held_and_not_logged`  (R14)
20. `Failure_channel_thresholds_are_independent_of_the_progress_channel`  (R14 — flood one, assert the other still emits on its own schedule)
21. `Final_summary_reports_failure_counts_by_exception_type`  (R14)
22. `Failure_type_breakdown_is_capped_and_buckets_the_remainder`  (R14)
23. `Last_held_failure_is_flushed_when_the_operation_ends`  (R14)

## 12. What the built library does differently

Seven things changed while building it. Each is a simplification the design did not anticipate
rather than a change of behaviour, and the sections above now describe the code.

| Design said | Code does | Why |
|---|---|---|
| `OperationSnapshot` is a `readonly record struct` | `sealed record` | ~20 members handed out in lists; copying costs more than the allocation |
| Winsorise the variance input at p99 | Clamp the deviation at 4 sigma | Same effect on the band without carrying a P-squared quantile sketch |
| An item disposed with no outcome is "Incomplete" | It is **failed**, tagged `(unrecorded)` | That path is an exception unwinding past the `using`; only the operation level keeps a distinct Incomplete state |
| (not specified) | `ThrottledLoggingOptions.OnEmitted` | Makes the public `ThrottledEvent` reachable, and lets tests assert on structure rather than log text |
| (not specified) | Held-back counters settle at emission time | An event still held is submitted but not yet classified; the counters balance once operations end |
| One progress template | Two templates sharing event id 9004 | A null estimate rendered through the numeric template reads `ETA s (–s)`; the warm-up case says `ETA not yet known` instead |
| (not specified) | `IOperationLogger.DefaultOptions` and a `configure` overload of `BeginOperation` | Options handed to `BeginOperation` replace the configured defaults wholesale, so a caller setting one property on a `new OperationOptions` silently lost the rest |

Two further notes from building it:

- **`OperationLogger` needs its DI constructor named explicitly.** It offers a second,
  non-DI constructor, and `ActivatorUtilities` cannot choose between them, so
  `AddThrottledLogging` registers it with an explicit factory.
- **Per-operation options replace the configured defaults; they are not merged into them.**
  `OperationOptions` has no way to tell "the caller left this alone" from "the caller wants this
  value", so `BeginOperation` takes what it is handed. `DefaultOptions` hands out a copy of the
  configured defaults to start from, and the `configure` overload does that for you.
- **The ETA warm-up gate is elapsed-time as well as sample-count.** Five completed items is not
  enough on its own; `EtaMinimumElapsed` (one second by default) must also have passed, or a loop
  of very fast items would publish an estimate built from nothing.

## 13. Context and host lifetime (0.2.0)

Nothing about an operation outlives its process, and that is still by design: persistence would
put I/O on the hot path. 0.2.0 instead makes sure that the context a host *does* keep reaches every
line, so runs can be joined up afterwards. Worked examples for Azure Functions and Application
Insights are in [`../azure-functions-and-application-insights.md`](../azure-functions-and-application-insights.md).

| Gap in 0.1.0 | 0.2.0 |
|---|---|
| Heartbeat lines from the sweeper thread lost the caller's logging scopes and `Activity`, so they had no invocation id and no `operation_Id` | `OperationScope` captures the `ExecutionContext` at `BeginOperation` and runs sweeps and the shutdown flush inside it. A caller that suppressed flow captures nothing, and those lines behave as before |
| No way to attach context to the library's lines without an outer scope | `OperationOptions.Scope`: key/value pairs copied at `BeginOperation` and opened as a scope around each line, as an immutable list of pairs, so structured sinks see fields and text sinks see `Key:Value, …` |
| Item (9004), failure (9005) and failure-summary (9006) lines named the operation but not its id, so two concurrent runs of one function could not be told apart | Every line carries `{OperationId}`. This changes the message text of those lines; the structured fields only gain one |
| Disposing `OperationLogger` stopped the sweeper and dropped whatever was held | `Dispose` flushes every running operation's held events and writes event **9008** ("still running when the logger shut down") with its counts. The operation stays open, because a function may still be draining |

The scope is opened per line rather than once per operation because logging scopes live in the
ambient context of whichever thread writes, and an operation's lines come from several threads.

Two further changes came out of reviewing this work, and they apply whether or not a host is involved:

- **The held event only moves forward.** Two submitters can take sequences 5 and 6 and publish in
  the opposite order; a plain exchange then left 5 held and lost 6 entirely. `ThrottleChannel`
  now publishes with a compare-and-swap that never replaces a later event with an earlier one.
- **Only the sweeper may write an already-written event.** Two submitters that both saw the count
  threshold could each pass the gate in turn and write the same event twice, the second time as
  `IsNew=false`, and the second write was counted as an extra emission. The "already written"
  check now happens under the gate for every reason except the sweeper's heartbeat, and a final
  flush waits for the gate instead of giving up. `ConcurrencyTests` caught this intermittently
  (about one run in eight on `main` before this change).

Every path that decides an operation's last lines (`End`, a sweep, the shutdown flush) takes one
lock per operation, so "still running" can never follow the operation's own end line, and a
heartbeat never follows it either. The lock covers writing to the logging providers but not the
`OnEmitted` observer: notifications made under it are collected and delivered once it is released,
so an observer that waits for another thread to end the operation cannot deadlock (a second Codex
pass raised this). A logging provider, which is called under the lock, must not block waiting for
the same operation to end. `End` retires the operation and releases the captured context
in a `finally`, so a throwing logging provider cannot strand it in the registry.

Shutdown is bounded against a provider that hangs on another thread. `Dispose` first flushes every
operation whose lock is free, then waits for the busy ones, sharing one five-second budget between
them; an operation still locked after that is skipped and reported as event 9009. A provider that
hangs on the disposing thread itself still hangs `Dispose`, since a synchronous call cannot be
abandoned. `BeginOperation` checks for disposal and registers the operation under the same lock
`Dispose` marks the logger disposed under, and holds the operation's lock until its entry line is
written, so an operation begun during shutdown is either refused or flushed after its entry line.
A sweep that throws is caught per operation and logged as event 9012, so the sweeper keeps running.

The lock is reentrant, because a logging provider called under it may end the operation or dispose
the logger. Observer notifications are therefore held until the outermost holder lets go, not
just the inner one. A shutdown flush that re-enters from inside the entry line is postponed until
that line has reached every provider. A second concurrent `Dispose` waits for the first to finish
(bounded), except on the same thread. An operation that captured no context is flushed in the
default context on the disposing thread, so shutdown never waits on the thread pool. Reloads run one
at a time and each reads the monitor's current value, so racing change callbacks cannot restore
older settings. The sweeper re-checks, under the channel gate, that its interval is still due, so
it cannot repeat an event a submitter wrote a moment before. An item completed after its operation
ended is ignored.
