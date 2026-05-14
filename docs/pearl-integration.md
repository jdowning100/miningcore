# CLAUDE.md — Pearl support in miningcore

This fork adds support for **Pearl** (PoUW chain, btcd fork — see
`arxiv.org/abs/2504.09971`) to miningcore. Pearl's shares are not Bitcoin
nonces; they are `PlainProof` blobs (matrix slice + Merkle proofs, ~276 KiB
raw / ~368 KiB base64 on the wire) that the pool verifies cryptographically.
On a hit that also meets the block target, an out-of-process sidecar
(`pearl-pool-service`) constructs the recursive Plonky2 ZK proof (~12 s) and
submits the block to `pearld` via standard `submitblock` JSON-RPC.

If you're another pool operator who wants to accept connections from the
`pearl-headless-cpp` miner, the **wire protocol spec is in
`docs/pearl-stratum-v1.md`** — this file is the operator-side overview of how
the miningcore fork implements it.

---

## 1. Architecture

```
miner (pearl-headless-cpp)
   │
   │   Stratum v1 + pearl/v1 capability (TCP, line-delimited JSON-RPC)
   │   mining.notify:    [job_id, prev_hash, incomplete_header_hex,
   │                      height, ntime, share_nbits, clean_jobs]
   │   mining.submit:    [worker, job_id, plain_proof_b64]
   │
   ▼
miningcore (this fork)
   │
   │   HTTP JSON-RPC to a local sidecar
   │   POST /v1/verify-share        (hot path,  ~10 ms)
   │   POST /v1/prove-and-submit    (cold path, ~12 s — block hits only)
   │
   ▼
pearl-pool-service        ── builds blocks, runs Plonky2 STARK prover,
   │                         calls pearld for getblocktemplate / submitblock
   │
   │   Bitcoin JSON-RPC (pearld is a btcd fork)
   ▼
pearld (Pearl full node)  ── verifies STARK, accepts block, advances chain
```

The miningcore process never builds a Pearl block or runs cryptography
itself — it Stratum-front-ends the miners and forwards share verification +
block construction to the sidecar. This is the same split as Quai's
`QuaiBitcoinJobManager` (which served as the structural template).

---

## 2. Files added / modified

### New (under `src/Miningcore/Blockchain/Bitcoin/`)

| File | Purpose |
|---|---|
| `PearlPool.cs` | Stratum pool class. Handles `mining.configure pearl/v1`, `mining.subscribe`, `mining.authorize`, address validation (bech32m HRPs `prl1`/`tprl1`/`sprl1`/`prtb1`), share submission routing. Extends `PoolBase`. |
| `PearlBitcoinJobManager.cs` | Job lifecycle + share verification. `SubmitShareAsync` POSTs to the sidecar's `/v1/verify-share`, threads the response back as Stratum result, and (on `meets_block=true`) fires the async `/v1/prove-and-submit` for STARK construction. Also defines `DifficultyToNBitsHex` (the canonical conversion from miningcore's `context.Difficulty` to Bitcoin-compact `share_nbits`). |
| `PearlBitcoinJob.cs` | Job state. Holds `IncompleteHeaderB64`, `JobId`, `Height`, `BlockDifficulty`. Drops everything Bitcoin-specific (no coinbase serialization, no merkle branch construction — those are sidecar concerns). |
| `DaemonResponses/PearlBlockTemplate.cs` | DTO for pearld's `getblocktemplate` response (the sidecar consumes it; miningcore just forwards it via `/v1/pool-template`). |
| `Configuration/PearlPoolConfigExtra.cs` | Pool-extra config: `poolServiceUrl`, `poolServiceTimeoutMs` (default 5000), `poolServiceProveTimeoutMs` (default 30000), `poolTemplatePollMs` (default 1000). |

### Modified

| File | Change | Reason |
|---|---|---|
| `src/Miningcore/Configuration/ClusterConfig.cs` | Add `[EnumMember(Value = "pearl")] Pearl` to `CoinFamily`; add `{CoinFamily.Pearl, typeof(BitcoinTemplate)}` to the family→template type map. | Register the new coin family. |
| `src/Miningcore/AutofacModule.cs` | `builder.RegisterType<PearlBitcoinJobManager>();` | Make the manager DI-resolvable. |
| `src/Miningcore/coins.json` | Add a `"pearl"` entry with `"family": "pearl"`. The `coinbaseHasher` / `headerHasher` / `blockHasher` fields are required by the `BitcoinTemplate` schema but *unused* — the sidecar does all hashing. `shareMultiplier=1`. | Coin metadata. |
| `src/Miningcore/Blockchain/Bitcoin/BitcoinPayoutHandler.cs` | Added `CoinFamily.Pearl` to the existing `[CoinFamily(...)]` attribute. | Pearl reuses btcwallet's `sendmany`/`sendtoaddress`/`gettransaction` unchanged. |
| `src/Miningcore/Stratum/StratumConnection.cs` | `MaxInboundRequestLength` raised from 32 KiB to 1 MiB. | A base64 PlainProof at mainnet shape is ~368 KiB. The previous 32 KiB cap would silently truncate Pearl shares. |

### Documentation

| File | Purpose |
|---|---|
| `docs/pearl-stratum-v1.md` | **The wire protocol spec.** Read this if you're implementing the pool side from scratch. Covers capability negotiation, all method shapes, difficulty math (the `target × h × w × k` bound), bandwidth notes, error codes, full annotated handshake transcript. |
| `CLAUDE.md` | This file — operator-side overview of the miningcore implementation. |

---

## 3. Wire protocol — the bits an operator must know

(Full spec: `docs/pearl-stratum-v1.md`. This section is the must-know
short version.)

### Capability negotiation
- Miner sends `mining.configure [["pearl/v1"], {}]`
- Pool **must** respond `{"pearl/v1": true, "pearl/v1.share_format": "base64"}`
- Miner disconnects if `pearl/v1` is not accepted

### One-shot params (sent right after subscribe, before any notify)

⚠️ **The values below are NOT configurable — they are baked into the
reference miner's CUDA kernel.** A pool that advertises any other shape
will produce shares the miner cannot construct. Pools MUST send exactly:

```json
{"id": null, "method": "pearl.set_mining_params",
 "params": [{
   "m": 131072, "n": 131072, "k": 4096, "rank": 128,
   "rows_pattern": [0, 8],                          // h = 2
   "cols_pattern": [0,1,8,9,16,17,24,25,            // w = 64 entries:
                    32,33,40,41,48,49,56,57,        // for i in 0..32:
                    64,65,72,73,80,81,88,89,        //   [8i, 8i+1]
                    96,97,104,105,112,113,120,121,
                    128,129,136,137,144,145,152,153,
                    160,161,168,169,176,177,184,185,
                    192,193,200,201,208,209,216,217,
                    224,225,232,233,240,241,248,249],
   "mma_type": "Int7xInt7ToInt32"                   // only int7×int7→int32 is wired
 }]}
```

Why these are non-negotiable:

- **`m` and `n` (131072 each)** — drive matrix allocations on the GPU; with
  the reference miner's tile sizes the VRAM budget for a default 8 GiB card
  is just sized to fit. Smaller values would run, larger would OOM.
- **`k` (4096)** — fixed for the consensus mining-config shape; must be a
  multiple of `rank` and of the kernel's `tile_k=128`.
- **`rank` (128)** — sparse-noise rank; the kernel's noise-generation path is
  built around this specific value.
- **`rows_pattern` / `cols_pattern`** — the kernel's hash-tile selection is
  compiled around these exact index lists (`default_rows_pattern()` and
  `default_cols_pattern()` in `miner/headless-cpp/src/main.cpp`).
- **`mma_type`** — the only MMA path implemented today; anything else makes
  the kernel reject the job.

Verifier consistency: `extract_difficulty_bound` in
`zk-pow/src/api/sanity_checks.rs:90` and the PlainProof byte format both
assume this exact shape. A pool serving different parameters will produce
proofs that fail `verify_plain_proof_with_target` even if a miner somehow
constructs them.

These values are also **static for the lifetime of the connection.** Pool
MUST disconnect if it ever needs to change them (a hard-fork-level event;
the protocol would bump to `pearl/v2`).

### `mining.notify`
```json
{"id": null, "method": "mining.notify",
 "params": [job_id,                       // str
            prev_hash_hex,                // 64-char hex
            incomplete_header_hex,        // 128-char hex (64-byte serialized IncompleteBlockHeader)
            height,                       // int
            ntime_hex,                    // 8-char hex (uint32 BE)
            share_nbits_hex,              // 8-char hex (Bitcoin compact nbits, per-worker)
            clean_jobs]}                  // bool
```

### `mining.submit`
```json
{"id": N, "method": "mining.submit",
 "params": [worker_name,                  // same as authorize
            job_id,                       // from mining.notify
            plain_proof_b64]}             // base64(PlainProof.to_bytes())
```

The `PlainProof` byte layout is the bincode serialization of the Rust struct
in `py-pearl-mining/src/lib.rs` (and `zk-pow::ffi::plain_proof::PlainProof`).
The canonical verifier is `pearl_mining.verify_plain_proof_with_target(...)`.
**Wire format must round-trip through that verifier byte-for-byte** — that's
the consensus oracle.

### Submit response
- `{"id": N, "result": true, "error": null}` — share accepted (block hit goes
  to async prove-and-submit; miner doesn't wait).
- `{"id": N, "result": null, "error": [code, "reason", null]}` — rejected.
  Codes: `20` low-diff, `21` stale, `23` job-not-found, `25` other.

### Difficulty math (the subtle bit)

The wire `share_nbits` is Bitcoin-compact. The miner converts to a raw target
and **multiplies by `h × w × k`** to get the `pow_target` its CUDA kernel
races against:

```
share_target = nbits_to_target(share_nbits)
pow_target   = share_target × h × w × k         # = ×524,288 for mainnet
```

The verifier (`pearl_mining.verify_plain_proof_with_target`) applies the same
multiplier on the pool side. Both sides arrive at `target × h × w × k`. If
your pool implementation skips this multiplier on either side you'll either
reject every share (target too tight) or accept every garbage submission
(target too loose).

Reference: `extract_difficulty_bound` in `zk-pow/src/api/sanity_checks.rs:90`.

---

## 4. Pool-side validation flow (what `SubmitShareAsync` actually does)

```
1. Parse mining.submit params  → {worker_name, job_id, plain_proof_b64}
2. Look up job_id in validJobs cache;
   - missing → throw StratumException(StratumError.JobNotFound, "job not found")
3. Convert worker's context.Difficulty → share_nbits via
   PearlBitcoinJobManager.DifficultyToNBitsHex(double)
4. POST {job_id, incomplete_header_b64, plain_proof_b64,
         share_nbits_hex, worker_id}
   to ${PoolServiceUrl}/v1/verify-share
5. Sidecar returns {ok, meets_share, meets_block, jackpot_hash_hex, reason}
   - !meets_share → throw StratumException(StratumError.LowDifficultyShare)
   - else → record share with Difficulty = stratum_difficulty,
            IsBlockCandidate = meets_block
6. If meets_block:
   - log "Pearl BLOCK candidate from ..."
   - POST {incomplete_header_b64, plain_proof_b64} to
     ${PoolServiceUrl}/v1/prove-and-submit  (no `await`; submitter races on)
   - On accepted → call OnBlockFound(), record TransactionConfirmationData
```

Credit is by **connection-level auth**, not per-share `workerName` — see
`PearlBitcoinJobManager.cs` lines 188–195. The submit's first param is
validated for non-empty but not used for credit. (A dev-fee miner that wants
its shares credited to a different address must open a second Stratum
connection authorized with the dev address.)

---

## 5. Behavioral fixes worth knowing about

These are not in stock miningcore. If you're porting this work to another
miningcore fork:

### 5.1 `LastActivity` update on share submit (PearlPool.cs:OnSubmitAsync)

Stock miningcore's `PoolBase.ZombieCheck` triggers `"Detected zombie-worker
(idle-timeout exceeded)"` and disconnects when
`clock.Now - context.LastActivity > poolConfig.ClientConnectionTimeout`.
**Every other coin family in miningcore updates `LastActivity` on share
submission** (look at `BitcoinPool.cs:211`, `KaspaPool.cs:263`,
`EthereumPool.cs:228`, etc.). The Pearl pool needs the same line:

```csharp
protected virtual async Task OnSubmitAsync(...)
{
    try
    {
        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        context.LastActivity = clock.Now;       // ← REQUIRED. Without this,
                                                //   the pool will boot every miner
                                                //   after `clientConnectionTimeout`
                                                //   seconds regardless of share rate.
        ...
    }
    ...
}
```

The repro is unmistakable: with `clientConnectionTimeout: 600`, every
connected miner gets booted at exactly 10 minutes despite continuously
submitting shares. Worth grepping for `LastActivity` in any new Pool subclass.

### 5.2 Stratum frame-size cap

`StratumConnection.MaxInboundRequestLength` is bumped from 32 KiB → 1 MiB.
A Pearl `mining.submit` carrying a base64 PlainProof is ~368 KiB. The 32 KiB
default silently truncates, and the only symptom is a quiet share-rejection
with no obvious error log on either side.

### 5.3 Bech32m HRP validation

`PearlPool.ValidateAddressAsync` accepts any of `prl1` (mainnet), `tprl1`
(testnet), `sprl1` (simnet), `prtb1` (regtest). If you want to restrict to a
single network in production, tighten this method.

### 5.4 Block-candidate detection requires per-share difficulty math, not the
header's nbits

`meets_block` is computed inside the sidecar by comparing the jackpot hash
against the block target derived from `incomplete_header.nbits`. Don't
double-check on miningcore side — the sidecar response is authoritative.

---

## 6. Sample pool config (`config-pearl-local.json`)

```jsonc
{
  "pools": [{
    "id": "pearl-pool-1",
    "enabled": true,
    "coin": "pearl",
    "address": "prl1pup...",                  // pool payout address
    "blockRefreshInterval": 1000,
    "jobRebroadcastTimeout": 15,
    "clientConnectionTimeout": 600,
    "extra": {
      "poolServiceUrl": "http://127.0.0.1:8341",
      "poolServiceTimeoutMs": 10000,
      "poolServiceProveTimeoutMs": 60000,
      "poolTemplatePollMs": 1000
    },
    "ports": {
      "3333": {
        "listenAddress": "0.0.0.0",
        "difficulty": 116400,                 // ~10 s per share on a 50 TH/s card
        "varDiff": {
          "minDiff": 16384,
          "maxDiff": 8388608,
          "targetTime": 10,
          "retargetTime": 30,
          "variancePercent": 30
        }
      }
    },
    "daemons": [{
      "host": "127.0.0.1", "port": 44107,
      "user": "rpcuser", "password": "rpcpass"
    }]
  }]
}
```

### Required dependencies running alongside miningcore

1. **`pearld`** (the chain node) — ports `44107` mainnet RPC / `44109` testnet RPC.
2. **`pearl-pool-service`** (Python sidecar) — listening on `:8341` by default. Env vars:
   - `PEARLD_RPC_URL`, `PEARLD_RPC_USER`, `PEARLD_RPC_PASSWORD`
   - `PEARLD_MINING_ADDRESS` — the address the pool pays the coinbase to
   - `PEARL_PROVER_LOCAL_BACKEND=cpu` — `cpu` is the only production-ready prover today
   - `RAYON_NUM_THREADS=16` — recommended on 16-core/32-thread hosts; 32 threads is no faster and adds SMT contention

---

## 7. Performance / capacity notes

- A single mainnet-shape PlainProof on the wire is ~368 KiB base64. At
  `vardiff target = 10 s` and 100 connected workers, that's ~3.7 MB/s
  inbound. Sized correctly for typical pool boxes; not negligible.
- STARK construction (`/v1/prove-and-submit`) takes ~12 s warm on a 5950X
  (full mainnet shape). During this window the sidecar's `/v1/verify-share`
  endpoint **MUST stay responsive** — concurrent share submissions are normal
  and should not block on the prove. The known correct path is for
  `pearl_mining.generate_proof()` and `verify_plain_proof_with_target()` to
  release the Python GIL via `py.allow_threads(...)` in `py-pearl-mining/src/lib.rs`.
  If your fork sees `code 20 internal error` on miners during block hits,
  this is likely the cause.
- Share verification (`/v1/verify-share`) is ~10 ms cold and ~5 ms warm on
  modern x86. It's a Rust call from Python over PyO3; under load the
  pool-service's `verify_threads` ThreadPoolExecutor (default 8 workers) is
  the bottleneck. Raise it if you serve more than ~50 active workers.

---

## 8. Build & run

```bash
# This fork builds with stock miningcore tooling:
dotnet publish -c Release -o build src/Miningcore/Miningcore.csproj

# Then:
./build/Miningcore -c config-pearl-local.json
```

Companion sidecar (separate repo / Python package):

```bash
PEARLD_RPC_URL=http://localhost:44107 \
PEARLD_RPC_USER=rpcuser PEARLD_RPC_PASSWORD=rpcpass \
PEARLD_MINING_ADDRESS=prl1pup... \
PEARL_PROVER_LOCAL_BACKEND=cpu \
RAYON_NUM_THREADS=16 \
uv run --package pearl-pool-service pearl-pool-service
```

---

## 9. Open items / known gaps in this fork

- **PPLNS payout scheme not yet adapted** — the existing
  `BitcoinPayoutHandler` (which Pearl reuses via the `CoinFamily` attribute)
  works for direct payouts via `sendmany`, but PPLNS share-window accounting
  for Pearl's `target × h × w × k` difficulty bound has not been hand-tuned.
  For pools that just want to collect blocks to their own wallet (no
  multi-miner reward sharing) this is unnecessary.
- **No miningcore unit tests for the Pearl manager.** The wire protocol has a
  documented "annotated handshake" in `docs/pearl-stratum-v1.md` that can be
  used as a regression harness.
- **`PearlPool` accepts shares submitted with a per-share `worker_name`
  different from the authorized worker** but credits to the connection-level
  authorized worker. If you want per-share crediting (useful for dev-fee
  patterns), modify `PearlBitcoinJobManager.SubmitShareAsync` lines 188–195
  to use `submitParams[0]` instead of `context.Worker`/`context.Miner`.

---

## 10. Where to read next

- `docs/pearl-stratum-v1.md` — wire protocol spec, the load-bearing
  reference for any other-pool implementation
- `pearl-pool-service` source — sidecar's HTTP API contract
- `zk-pow/src/api/verify.rs` and `py-pearl-mining/src/lib.rs` — canonical
  verifier (`verify_plain_proof_with_target`) and prover (`generate_proof`)
  entry points
- `miner/headless-cpp/` — the reference miner the protocol was designed
  around. Useful to read its `StratumClient` to see exactly what a Pearl
  miner expects to see on the wire.
