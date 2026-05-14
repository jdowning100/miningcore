# `pearl/v1` — Stratum Extension for Pearl Mining

Status: implemented in this fork. Reference miner: `pearl-headless-miner --stratum`.
Reference pool: `miningcore` with `"family": "pearl"` coins.

This document specifies the Stratum-v1 extension used by Pearl mining pools.
Pearl's PoW is a noisy int7×int7→int32 GEMM with a STARK proof of correctness
(see arxiv.org/abs/2504.09971); the chain consensus rule requires a recursive
Plonky2 proof per accepted block. To make pools tractable, the share artifact
is the cheap **PlainProof** (matrix slice + Merkle proofs; current C++ mainnet
proofs measure around 276 KB raw and around 368 KB after base64 on the Stratum
wire); the pool constructs the
expensive STARK only when a share also meets the block target.

The protocol is a small, mostly-additive extension of Stratum v1: the wire
framing is unchanged (line-delimited JSON-RPC 2.0), only a handful of methods
have Pearl-specific shapes.

## Capability negotiation

The miner advertises support for this protocol on its first frame:

```json
{"id": 1, "method": "mining.configure",
 "params": [["pearl/v1"], {}]}
```

The pool responds with the capabilities it accepts:

```json
{"id": 1, "result": {"pearl/v1": true,
                     "pearl/v1.share_format": "base64"},
          "error": null}
```

If the pool does not include `"pearl/v1": true`, the miner MUST disconnect; the
pool will not accept Pearl shares.

`pearl/v1.share_format` is currently always `"base64"` (PlainProof bincode
bytes encoded directly as base64). Future formats (e.g. length-prefixed binary
frames) will introduce a new capability string `pearl/v2`.

## Subscribe and authorize

These follow standard Stratum v1 conventions. `mining.subscribe` returns the
usual 2-tuple of `(subscriptions, extranonce1)`; the extranonce fields are
present but unused (set to `""` and `0` respectively — Pearl shares are
self-contained).

```json
{"id": 2, "method": "mining.subscribe",
 "params": ["pearl-headless-miner/0.1"]}

{"id": 2, "result": [[["mining.set_difficulty", "<conn>"],
                      ["mining.notify",         "<conn>"]],
                     "",
                     0],
          "error": null}
```

`mining.authorize` accepts a Pearl payout address as the worker name (typical
shape `"prl1pup...rig1"`, where the suffix after the first `.` is a worker
label) and any password (often used to convey a static difficulty via
`"x;d=4096"`).

## `pearl.set_mining_params` (one-shot notification)

Immediately after subscribe, the pool sends a single non-RPC notification
carrying the pool-wide mining config. The miner uses these values to
configure its kernel before the first job arrives:

```json
{"id": null,
 "method": "pearl.set_mining_params",
 "params": [{
     "m": 131072,
     "n": 131072,
     "k": 4096,
     "rank": 128,
     "rows_pattern": [0, 8],
     "cols_pattern": [0, 1, 8, 9, /* ... 64 entries ... */ 248, 249],
     "mma_type": "Int7xInt7ToInt32"
 }]}
```

**This is not just a comparability rule — these values are baked into the
miner.** The reference Pearl miner (`pearl-headless-cpp`) has the mining
config compiled into its CUDA kernel: tile sizes, sparse-noise patterns,
rank-128 noise generation, `Int7xInt7ToInt32` tensor-core path, and the
`rows_pattern` / `cols_pattern` hash layout are all fixed at build time.
A pool that sends *anything other than the values shown above* will produce
shares the miner cannot construct and proofs the consensus verifier will
reject. **Pools MUST advertise exactly:**

```
m              = 131072
n              = 131072
k              = 4096
rank           = 128
rows_pattern   = [0, 8]                              (h = 2 entries)
cols_pattern   = [0,1,8,9,16,17,24,25,...,248,249]   (w = 64 entries, alternating singletons)
mma_type       = "Int7xInt7ToInt32"
```

The exact `cols_pattern` is: `for i in 0..32: [8i, 8i+1]` (i.e.
`[0,1, 8,9, 16,17, ..., 248,249]`). Confirm against the reference
implementation if your encoder differs.

If a future hard-fork changes the mainnet mining config, miners will need to
be rebuilt for the new config — the protocol's `pearl/v1` capability string
ties the wire format to one fixed shape. A new shape would be served as
`pearl/v2` (or similar) so old miners cleanly disconnect rather than mining
garbage.

These values are also **static for the lifetime of the connection.** If the
pool needs to change the mining config it MUST disconnect all miners and
re-handshake.

## `mining.notify`

Replaces the standard Bitcoin coinbase/merkle-branch params. Pearl jobs carry
a pre-serialized **incomplete header** (the 64-byte chain header without the
proof commitment) and a per-worker share-target `nbits`.

```json
{"id": null,
 "method": "mining.notify",
 "params": ["job-1",                           // job_id
            "00...054",                        // prev_hash_hex (32 bytes hex)
            "01000000...000000",               // incomplete_header_hex (64 bytes hex)
            51500,                             // height (integer)
            "66666666",                        // ntime_hex (uint32 big-endian)
            "1d2fffff",                        // share_nbits_hex (Bitcoin compact, 4 bytes)
            true]}                             // clean_jobs
```

`share_nbits_hex` is the per-worker share target encoded as Bitcoin compact
nbits. See **Difficulty math** below for the full path from miningcore's
`context.Difficulty` to the kernel's `pow_target`.

`clean_jobs` follows the standard Stratum semantic: when `true`, the miner
should discard any in-flight work and restart on this job.

### Difficulty math

The pool emits a Bitcoin-compact `share_nbits`. The miner must do **two
conversions** before the kernel can use it:

```
miningcore vardiff
   → context.Difficulty (Bitcoin "diff-1" units; float)
   → share_target_u256 = diff1 / context.Difficulty
   → share_nbits = Target(share_target_u256).ToCompact()
   → emitted on wire ──────────────────────────────────
                                                       │
                                                       ▼ (miner)
                                       _nbits_to_target(share_nbits)
                                       = share_target_u256
                                                       │
                                                       ▼ multiply by h*w*k
                                  pow_target = share_target × h × w × k
                                                       │
                                                       ▼ (CUDA kernel compares hashes ≤ pow_target)
```

**Critical detail**: the kernel-side `pow_target` is `share_target × h × w × k`,
where the multiplier comes from the mining config. This is Pearl's PoUW
difficulty bound — each transcript-hash candidate represents `h × w × k` int8
MACs of work. Pearl's `mining_config` defines these:

- `h` = `mining_config.rows_pattern.length` (typically 2)
- `w` = `mining_config.cols_pattern.length` (typically 64)
- `k` = `mining_config.k` (typically 4096)

For mainnet defaults: `h × w × k = 2 × 64 × 4096 = 524,288`.

The sidecar's `pearl_mining.verify_plain_proof_with_target(...)` applies the
same multiplier internally via `extract_difficulty_bound(nbits, config)` (see
`zk-pow/src/api/sanity_checks.rs`), so both sides — kernel and verifier —
arrive at the same `target × h × w × k` bound and the comparison is symmetric.

Reference implementations:

- **Pool → nbits**: `PearlBitcoinJobManager.DifficultyToNBitsHex(double)`
  (`Blockchain/Bitcoin/PearlBitcoinJobManager.cs`)
- **Miner: nbits → kernel target**: `_nbits_to_target` in
  `headless_miner.stratum_client` followed by `MiningJob.adjust_target(...)`
  in `pearl-gateway/comm/dataclasses.py`
- **Sidecar verifier**: `extract_difficulty_bound` in
  `zk-pow/src/api/sanity_checks.rs:90`

## `mining.set_difficulty`

Sent before / between jobs to nudge per-worker difficulty up/down. The miner
SHOULD treat it as advisory only — the authoritative share target lives in
the most recent `mining.notify` frame's `share_nbits_hex`.

```json
{"id": null,
 "method": "mining.set_difficulty",
 "params": [4096.0]}
```

## `mining.submit`

Three params; no extranonce, no nonce — the PlainProof is self-contained:

```json
{"id": 17,
 "method": "mining.submit",
"params": ["prl1pup...rig1",        // worker_name (same as authorize)
            "job-1",                 // job_id from mining.notify
            "<plain_proof_b64>"]}    // PlainProof.to_bytes() → base64
```

Wire encoding of the PlainProof:

```python
import base64
plain_proof_b64 = base64.b64encode(plain_proof.to_bytes()).decode("ascii")
```

The decoded `PlainProof.to_bytes()` is the bincode serialization of
`zk_pow::ffi::plain_proof::PlainProof` (see `py-pearl-mining/src/lib.rs`).

The pool replies with the standard Stratum success/error shape:

- `{"id": 17, "result": true, "error": null}` — share accepted (also a block
  if it meets the block target; the pool will construct the STARK
  asynchronously and submit to pearld).
- `{"id": 17, "result": null, "error": [20, "stale share", null]}` —
  rejected. Error codes follow Stratum convention; common reasons:
  - `20` low-difficulty share (doesn't meet `share_nbits`)
  - `21` stale share (job_id not in cache)
  - `23` job not found
  - `25` other (e.g. malformed `plain_proof_b64`)

## Pool-side validation flow

1. Parse the three params; reject malformed input with error 25.
2. Look up `job_id` in the pool's job cache; reject 21/23 if missing.
3. Convert the worker's current pool-side difficulty to `share_nbits` (use
   `NBitcoin.Target.ToCompact()` or equivalent).
4. POST `{job_id, incomplete_header_b64, plain_proof_b64, share_nbits_hex,
   worker_id}` to `pearl-pool-service /v1/verify-share`. The sidecar
   decodes, runs `pearl_mining.verify_plain_proof_with_target`, and returns
   `(meets_share, meets_block, jackpot_hash_hex, reason)`.
5. If `meets_share == false`, return Stratum error 20.
6. Record the share with `difficulty = stratum_difficulty`, mark
   `IsBlockCandidate = meets_block`.
7. If `meets_block == true`, asynchronously POST
   `{incomplete_header_b64, plain_proof_b64}` to the sidecar's
   `/v1/prove-and-submit` (typical latency 3–10 s; the share-handling path
   does not block on this).

## Bandwidth

A default-mainnet (`m=n=131072, k=4096, h=2, w=64`) PlainProof is around
276 KB raw in the current C++ standalone miner path, or around 368 KB after
base64. At a sensible vardiff (1 share/30 s/worker) and a small pool of
100 workers, inbound is roughly 1.2 MB/s — still within typical pool sizing,
but not negligible.

Both ends must raise their default frame limits:

- **Pool (miningcore)**: `Mining/StratumConnection.cs` default line-length
  may need to be raised to ~1 MB explicitly for Pearl pools. The integration
  in this fork inherits whatever miningcore's default is; if you see truncated
  shares in logs, bump the cap.
- **Sidecar (`pearl-pool-service`)**: aiohttp is configured with
  `client_max_size = 4 * 1024 * 1024` in `service.py:create_app`.

## Full worked example (annotated handshake)

This is a literal transcript of the first 6 frames between a miner and pool,
abbreviated for readability. Each line is one complete JSON-RPC frame
followed by a newline. `>` is miner → pool, `<` is pool → miner.

```
> {"id":1,"method":"mining.configure","params":[["pearl/v1"],{}]}
< {"id":1,"result":{"pearl/v1":true,"pearl/v1.share_format":"base64"},"error":null}

> {"id":2,"method":"mining.subscribe","params":["pearl-headless-miner/0.1"]}
< {"id":2,"result":[[["mining.set_difficulty","conn-7"],["mining.notify","conn-7"]],"",0],"error":null}
< {"id":null,"method":"pearl.set_mining_params","params":[{
    "m":131072,"n":131072,"k":4096,"rank":128,
    "rows_pattern":[0,8],
    "cols_pattern":[0,1,8,9, /* … 64 entries … */ 248,249],
    "mma_type":"Int7xInt7ToInt32"}]}

> {"id":3,"method":"mining.authorize","params":["prl1pup37yfc8e0cg5gc4kg2laqdt6z5nkgrguun2mqskuhgjl799wacsa4qh7x.rig1","x"]}
< {"id":3,"result":true,"error":null}
< {"id":null,"method":"mining.set_difficulty","params":[4096.0]}
< {"id":null,"method":"mining.notify","params":[
    "0000a1f3",                          // job_id
    "00...0a054",                        // prev_hash_hex (32 bytes)
    "0000002054000000…",                 // incomplete_header_hex (64 bytes)
    51500,                               // height
    "66666666",                          // ntime_hex
    "1d2fffff",                          // share_nbits_hex  ← per-worker share target
    true                                 // clean_jobs
  ]}

# miner runs the GEMM, finds a hit, encodes the PlainProof:
#     plain_proof_b64 = base64.b64encode(plain_proof.to_bytes()).decode()

> {"id":4,"method":"mining.submit","params":[
    "prl1pup37yfc8e0cg5gc4kg2laqdt6z5nkgrguun2mqskuhgjl799wacsa4qh7x.rig1",
    "0000a1f3",
    "NwAAAAAAAAC…"                   // base64 of PlainProof bytes
  ]}
< {"id":4,"result":true,"error":null}    // share accepted; possibly also a block hit
```

Notes on the example:

- The pool MAY interleave a `mining.set_difficulty` and a `mining.notify`
  right after `mining.authorize` (as shown) so the miner gets work immediately.
- `share_nbits_hex = "1d2fffff"` corresponds to a raw target of
  ~`0x2fffff × 2^208`. At the mainnet `h×w×k = 524,288` multiplier, the
  effective hash bound the miner is racing against is
  `target × 524288`. The miner's kernel uses the multiplied value as its
  `pow_target` (see Difficulty math above).
- `clean_jobs=true` indicates this is a fresh block; the miner should
  drop any in-flight tile evaluation and restart.
- The pool replies to `mining.submit` with `true` for *any* share that meets
  the worker's share target — even if it also meets the block target
  (block construction proceeds asynchronously in the sidecar).

## Versioning

If the wire format changes (e.g. binary framing, new fields, different
compression), increment to `pearl/v2` and reject miners that don't send it.

## Reference implementations in this repository

- Pool side: `miningcore/src/Miningcore/Blockchain/Bitcoin/Pearl*.cs`
- Sidecar:   `miner/pearl-pool-service/`
- Miner:     `miner/headless-miner/src/headless_miner/stratum_client.py`
- Wire test: `miner/headless-miner/tests/test_stratum_client.py`
