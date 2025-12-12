# Quai SHA256 Integration Guide for Miningcore

This document details how to integrate Quai token SHA256 mining into miningcore using the custom `quai_getBlockTemplate` RPC.

## Table of Contents

1. [Overview](#overview)
2. [Quai RPC Response Format](#quai-rpc-response-format)
3. [Byte Order Handling (Critical!)](#byte-order-handling-critical)
4. [Implementation Steps](#implementation-steps)
5. [Key Code Changes](#key-code-changes)
6. [ASICBoost Support](#asicboost-support)
7. [Gotchas and Common Pitfalls](#gotchas-and-common-pitfalls)

---

## Overview

### What Makes Quai Different

Standard Bitcoin pools use `getBlockTemplate` which returns:
- Full transaction list
- Pool builds coinbase transaction from scratch
- Pool computes merkle branches from transaction hashes

Quai's `quai_getBlockTemplate` returns:
- **Pre-built coinbase parts** (`coinb1`, `coinb2`)
- **Pre-computed merkle branches** (`merklebranch`)
- Pool only needs to insert extranonces and compute merkle root

### Reference Implementation

The Go implementation in `/Users/jonathan/alphapool-soap-stratum/` serves as the reference:
- `proxy/stratum_v1.go` - Stratum protocol handling
- `proxy/stratum_v1_crypto.go` - Share verification and header construction
- `proxy/proxy.go` - Template management
- `rpc/rpc.go` - RPC response structures

---

## Quai RPC Response Format

### RPC Call

```json
{
  "jsonrpc": "2.0",
  "method": "quai_getBlockTemplate",
  "params": [{
    "rules": ["sha"],
    "coinbase": "0x00480ee0365A96540C214AeEddF8ecA7AcD17F0B"
  }],
  "id": 1
}
```

### Response Structure

```json
{
  "bits": "1d00ffff",
  "coinb1": "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff",
  "coinb2": "ffffffff0100f2052a0100000023210279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798ac00000000",
  "coinbaseAuxExtraBytesLength": 20,
  "curtime": 1702300000,
  "extranonce1Length": 4,
  "extranonce2Length": 8,
  "height": 850000,
  "merklebranch": [
    "0xabc123...def456",
    "0x789012...345678"
  ],
  "merkleroot": "0x...",
  "mintime": 1702299000,
  "noncerange": "00000000ffffffff",
  "previousblockhash": "0x00000000000000000002a7c4c1e48d76c5a37902165a270156b7a8d72728a054",
  "quaiheight": 12345,
  "quairoot": "0x...",
  "sigoplimit": 80000,
  "sizelimit": 4000000,
  "target": "00000000ffff0000000000000000000000000000000000000000000000000000",
  "version": 536870912
}
```

### Key Fields

| Field | Description | Byte Order |
|-------|-------------|------------|
| `previousblockhash` | Previous block hash | **Big-endian** hex |
| `merklebranch[]` | Pre-computed merkle branches | **Big-endian** hex |
| `coinb1` | Coinbase part 1 (before extranonces) | Raw bytes as hex |
| `coinb2` | Coinbase part 2 (after extranonces) | Raw bytes as hex |
| `bits` | Compact difficulty target | Big-endian hex |
| `target` | Full 32-byte target | Big-endian hex |
| `version` | Block version integer | Native uint32 |
| `curtime` | Block timestamp | Native int64 |

---

## Byte Order Handling (Critical!)

This is the most confusing aspect of Bitcoin/stratum mining. Here's a complete breakdown:

### Terminology

| Term | Description |
|------|-------------|
| **BE (Big-Endian)** | Most significant byte first. How humans read hex: `0x12345678` |
| **LE (Little-Endian)** | Least significant byte first. How x86 CPUs store: `78 56 34 12` |
| **swap256** | Reverse order of 8 uint32 words (32 bytes). Used for prevhash in mining.notify |
| **reverseBytes** | Full byte reversal. `[A,B,C,D]` → `[D,C,B,A]` |
| **flip80 / bswap_32** | Byte-swap within each 4-byte word. Used before hashing header |

### Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           QUAI RPC RESPONSE                                  │
│  previousblockhash: BE hex    merklebranch[]: BE hex    coinb1/coinb2: raw  │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         TEMPLATE PROCESSING                                  │
│                                                                              │
│  previousblockhash:  BE → swap256 → for mining.notify                       │
│  merklebranch[]:     BE → reverseBytes → LE (for MerkleTree.WithFirst)      │
│  coinb1/coinb2:      Store as raw bytes (no conversion needed)              │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           MINING.NOTIFY                                      │
│                                                                              │
│  [job_id, prevhash_swapped, coinb1_hex, coinb2_hex,                         │
│   merklebranches_LE_hex, version_hex, bits_hex, ntime_hex, clean]           │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      MINER SUBMITS SHARE                                     │
│                                                                              │
│  [worker, job_id, extranonce2, ntime, nonce, version_bits?]                 │
└─────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                       SHARE VERIFICATION                                     │
│                                                                              │
│  1. Build coinbase: coinb1 + extranonce1 + extranonce2 + coinb2             │
│  2. Hash coinbase: SHA256d(coinbase) → coinbaseHash (LE)                    │
│  3. Compute merkle root: MerkleTree.WithFirst(coinbaseHash) → merkleRoot    │
│  4. Build 80-byte header (see below)                                        │
│  5. Apply flip80 (bswap each uint32)                                        │
│  6. SHA256d(header_flipped) → blockHash                                     │
│  7. Compare blockHash vs target                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Previous Block Hash Handling

```
RPC returns (BE):     00000000000000000002a7c4c1e48d76c5a37902165a270156b7a8d72728a054

Decode to bytes:      [00 00 00 00 00 00 00 00 00 02 a7 c4 c1 e4 8d 76
                       c5 a3 79 02 16 5a 27 01 56 b7 a8 d7 27 28 a0 54]

Apply swap256:        Reverse order of 8 uint32 words (NOT byte-swap within words)
                      Word 0 ↔ Word 7, Word 1 ↔ Word 6, etc.

Result for notify:    [56 b7 a8 d7 27 28 a0 54 c5 a3 79 02 16 5a 27 01
                       00 02 a7 c4 c1 e4 8d 76 00 00 00 00 00 00 00 00]

This matches miningcore's ReverseByteOrder() function!
```

### Merkle Branch Handling

```
RPC returns (BE):     0xabc123...def456 (32 bytes)

For MerkleTree.WithFirst(), branches must be LE:
                      reverseBytes(branch_BE) → branch_LE

Go code (proxy.go:969-973):
    for i := 0; i < 32; i++ {
        branchLE[i] = branchBE[31-i]
    }

Miningcore equivalent:
    branch.HexToByteArray().ReverseInPlace()
```

### 80-Byte Header Construction

The Go code builds the header in a specific way to account for `flip80`:

```
Header Layout (80 bytes):
┌──────────┬──────────────┬────────────┬──────────┬──────────┬──────────┐
│ Version  │ PrevHash     │ MerkleRoot │ Time     │ Bits     │ Nonce    │
│ 4 bytes  │ 32 bytes     │ 32 bytes   │ 4 bytes  │ 4 bytes  │ 4 bytes  │
└──────────┴──────────────┴────────────┴──────────┴──────────┴──────────┘

Go buildV1BlockHeader() process:
1. Write version as BE uint32
2. PrevHash: BE → reverseBytes → LE, then bswap each word back to BE
   (This pre-compensates for flip80)
3. MerkleRoot: LE, then bswap each word to BE (pre-compensates for flip80)
4. Time, Bits, Nonce: Write as BE uint32

5. Apply flip80 (bswap each uint32 word)
   - This converts all BE uint32s to LE for actual hashing

6. SHA256d(header_after_flip80)
```

### Why flip80?

Bitcoin miners receive header fields and reconstruct the header. The stratum protocol sends values as hex strings representing integers (e.g., version "20000000" = 0x20000000).

The miner:
1. Parses hex strings as integers
2. Writes them to header in big-endian byte order
3. Applies flip80 (byte-swap each uint32) before hashing

The pool must do the same when verifying shares.

**Miningcore approach**: Uses NBitcoin's `BlockHeader.ToBytes()` which handles all this internally, producing the correctly serialized header ready for hashing.

---

## Implementation Steps

### Step 1: Create Response DTO

Create `src/Miningcore/Blockchain/Bitcoin/DaemonResponses/QuaiBlockTemplate.cs`:

```csharp
public class QuaiBlockTemplate
{
    public string Bits { get; set; }
    public string Coinb1 { get; set; }
    public string Coinb2 { get; set; }
    public int CoinbaseAuxExtraBytesLength { get; set; }
    public long CurTime { get; set; }
    public int Extranonce1Length { get; set; }
    public int Extranonce2Length { get; set; }
    public long Height { get; set; }

    [JsonProperty("merklebranch")]
    public string[] MerkleBranch { get; set; }

    [JsonProperty("merkleroot")]
    public string MerkleRoot { get; set; }

    public long MinTime { get; set; }
    public string NonceRange { get; set; }

    [JsonProperty("previousblockhash")]
    public string PreviousBlockHash { get; set; }

    public long QuaiHeight { get; set; }
    public string QuaiRoot { get; set; }
    public string Target { get; set; }
    public uint Version { get; set; }
}
```

### Step 2: Modify MerkleTree.cs

Add constructor for pre-computed branches:

```csharp
/// <summary>
/// Creates a merkle tree from pre-computed branches.
/// </summary>
/// <param name="precomputedBranches">Merkle branches (already in LE format)</param>
public MerkleTree(IList<byte[]> precomputedBranches)
{
    Steps = precomputedBranches;
}
```

### Step 3: Create QuaiBitcoinJob Class

```csharp
public class QuaiBitcoinJob : BitcoinJob
{
    private byte[] coinb1;
    private byte[] coinb2;
    private int extranonce1Length;
    private int extranonce2Length;

    public void Init(QuaiBlockTemplate template, string jobId, ...)
    {
        // Store coinbase parts
        coinb1 = template.Coinb1.HexToByteArray();
        coinb2 = template.Coinb2.HexToByteArray();
        extranonce1Length = template.Extranonce1Length > 0 ? template.Extranonce1Length : 4;
        extranonce2Length = template.Extranonce2Length > 0 ? template.Extranonce2Length : 8;

        // Convert merkle branches from BE to LE
        var branchesLE = template.MerkleBranch
            .Select(hex => hex.TrimPrefix("0x").HexToByteArray().ReverseInPlace())
            .ToList();

        mt = new MerkleTree(branchesLE);

        // Build job params for mining.notify
        previousBlockHashReversedHex = template.PreviousBlockHash
            .TrimPrefix("0x")
            .HexToByteArray()
            .ReverseByteOrder()  // swap256 equivalent
            .ToHexString();

        merkleBranchesHex = branchesLE
            .Select(b => b.ToHexString())
            .ToArray();

        // ... rest of initialization
    }

    protected override byte[] SerializeCoinbase(string extraNonce1, string extraNonce2)
    {
        // Simple concatenation: coinb1 + extranonce1 + extranonce2 + coinb2
        var extraNonce1Bytes = extraNonce1.HexToByteArray();
        var extraNonce2Bytes = extraNonce2.HexToByteArray();

        using var stream = new MemoryStream();
        stream.Write(coinb1);
        stream.Write(extraNonce1Bytes);
        stream.Write(extraNonce2Bytes);
        stream.Write(coinb2);
        return stream.ToArray();
    }
}
```

### Step 4: Create QuaiBitcoinJobManager

```csharp
public class QuaiBitcoinJobManager : BitcoinJobManagerBase<QuaiBitcoinJob>
{
    protected override object[] GetBlockTemplateParams()
    {
        return new object[]
        {
            new
            {
                rules = new[] { "sha" },
                coinbase = poolConfig.Address
            }
        };
    }

    protected override async Task<RpcResponse<QuaiBlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        return await rpc.ExecuteAsync<QuaiBlockTemplate>(logger,
            "quai_getBlockTemplate", ct, GetBlockTemplateParams());
    }

    // Override UpdateJob to use QuaiBlockTemplate
}
```

### Step 5: Add Coin Configuration

In `coins.json`:

```json
"quai": {
    "name": "Quai",
    "symbol": "QUAI",
    "family": "bitcoin",
    "coinbaseHasher": { "hash": "sha256d" },
    "headerHasher": { "hash": "sha256d" },
    "blockHasher": {
        "hash": "reverse",
        "args": [{ "hash": "sha256d" }]
    },
    "explorerBlockLink": "https://quaiscan.io/block/$hash$",
    "explorerTxLink": "https://quaiscan.io/tx/{0}"
}
```

---

## ASICBoost Support

**Good news**: Miningcore already fully supports ASICBoost (Overt ASIC Boost / Version Rolling).

### Existing Implementation

- `BitcoinStratumExtensions.VersionRolling` handles `mining.configure`
- `BitcoinConstants.VersionRollingPoolMask = 0x1fffe000`
- `BitcoinWorkerContext.VersionRollingMask` stores per-worker mask
- `BitcoinJob.SerializeHeader()` applies version bits

### How It Works

1. Miner sends `mining.configure` with `version-rolling` extension
2. Pool responds with allowed mask (0x1fffe000 = bits 13-28)
3. Miner submits share with 6th parameter: `version_bits`
4. Pool validates bits are within mask, applies to header version

No changes needed for Quai integration - it inherits this automatically.

---

## Gotchas and Common Pitfalls

### 1. Merkle Branch Byte Order

**Problem**: Quai RPC returns merkle branches in big-endian. `MerkleTree.WithFirst()` expects little-endian.

**Solution**: Always reverse each branch when processing RPC response:
```csharp
var branchLE = branchHex.HexToByteArray().ReverseInPlace();
```

### 2. PrevHash for mining.notify

**Problem**: Different from simple reversal. Need word-order swap, not byte swap.

**Solution**: Use `ReverseByteOrder()` which:
1. Reads 8 uint32 words
2. Converts each to big-endian
3. Reverses the entire array

This is equivalent to Go's `swap256()`.

### 3. Coinbase Construction

**Problem**: Standard miningcore builds coinbase from scratch. Quai provides pre-built parts.

**Solution**: Override `SerializeCoinbase()`:
```csharp
coinb1 + extranonce1 + extranonce2 + coinb2
```

### 4. Extranonce Lengths

**Problem**: Quai RPC specifies extranonce lengths. Must match exactly.

**Solution**:
- Read `extranonce1Length` and `extranonce2Length` from RPC
- Ensure `ExtraNonceProvider` generates correct length
- Validate submitted extranonce2 length

### 5. Block Submission

**Problem**: Quai uses different RPC for block submission (`quai_submitShaBlock`).

**Solution**: Override `SubmitBlockAsync()`:
```csharp
await rpc.ExecuteAsync(logger, "quai_submitShaBlock", ct, new[] { blockHex });
```

### 6. Empty Merkle Branches

**Problem**: Some miners (Braiins) fail if `merkle_branch` is `null` instead of `[]`.

**Solution**: Always initialize as empty array:
```csharp
merkleBranchesHex = new string[0];  // Not null!
```

### 7. Target vs Bits

**Problem**: Confusion between `bits` (compact) and `target` (full 32-byte).

**Clarification**:
- `bits`: 4-byte compact representation, used in header
- `target`: Full 32-byte target for share validation
- Both are big-endian from RPC

### 8. Stale Share Detection

**Problem**: Quai has `quaiheight` for internal state changes.

**Solution**: Track both `previousblockhash` changes (new block, clean=true) AND `quaiheight` changes (state update, may need clean=true).

### 9. NBitcoin Header Serialization

**Problem**: NBitcoin's `BlockHeader.ToBytes()` handles byte ordering automatically. Don't double-convert.

**Solution**: When using NBitcoin:
- Pass `PreviousBlockHash` as-is (BE hex from RPC)
- `uint256.Parse()` handles conversion internally
- `ToBytes()` produces correctly serialized header

### 10. Share Difficulty Calculation

Quai uses standard Bitcoin difficulty:
```
Diff1 Target = 0x00000000ffff0000000000000000000000000000000000000000000000000000
Diff1 Hashes = 2^32 = 4294967296

shareDiff = Diff1Target / hashAsInt
```

---

## Testing Checklist

- [ ] RPC connection to Quai node works
- [ ] Block template parsing succeeds
- [ ] Merkle branches correctly converted BE→LE
- [ ] PrevHash correctly word-swapped for mining.notify
- [ ] Coinbase serialization: coinb1 + en1 + en2 + coinb2
- [ ] Share verification produces correct hash
- [ ] ASICBoost version rolling works
- [ ] Block submission via `quai_submitShaBlock`
- [ ] Stale detection on prevhash AND quaiheight changes

---

## File Reference

### Miningcore Files to Modify/Create

| File | Action | Purpose |
|------|--------|---------|
| `Blockchain/Bitcoin/DaemonResponses/QuaiBlockTemplate.cs` | Create | RPC response DTO |
| `Crypto/MerkleTree.cs` | Modify | Add constructor for pre-computed branches |
| `Blockchain/Quai/QuaiBitcoinJob.cs` | Create | Job with coinb1/coinb2 handling |
| `Blockchain/Quai/QuaiBitcoinJobManager.cs` | Create | Template fetching and job management |
| `Blockchain/Quai/QuaiBitcoinPool.cs` | Create | Pool coordination (optional, can extend BitcoinPool) |
| `coins.json` | Modify | Add Quai coin definition |

### Go Reference Files

| File | Key Functions |
|------|---------------|
| `rpc/rpc.go` | `BlockTemplateResponse` struct, `GetBlockTemplate()` |
| `proxy/stratum_v1.go` | `sendV1Job()`, `convertToV1Template()` |
| `proxy/stratum_v1_crypto.go` | `verifyV1Share()`, `buildV1BlockHeader()`, `swap256()`, `flip80()` |
| `proxy/proxy.go` | `fetchV1Template()`, template caching |

---

## Building and Running on Ubuntu

### Prerequisites

Install required dependencies on Ubuntu:

```bash
# .NET 6.0 SDK
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-6.0

# Build dependencies for native libraries
sudo apt install -y build-essential cmake libssl-dev libboost-all-dev libsodium-dev

# PostgreSQL (for miningcore database)
sudo apt install -y postgresql postgresql-contrib

# Go 1.21+ (for quai-payout-service)
sudo snap install go --classic
# Or download from https://go.dev/dl/
```

### Building Miningcore

```bash
cd /home/jonathan/miningcore

# Build native libraries (libmultihash, etc.)
cd src/Miningcore/Native
./build-libs-linux.sh
cd ../../..

# Build miningcore (Debug mode for development)
dotnet build src/Miningcore -c Debug

# Or Release mode for production
dotnet build src/Miningcore -c Release
```

The built binary will be at:
- Debug: `src/Miningcore/bin/Debug/net6.0/Miningcore.dll`
- Release: `src/Miningcore/bin/Release/net6.0/Miningcore.dll`

### Running Miningcore

```bash
# Using the debug script (recommended for development)
./build/run-debug.sh

# Or manually with a specific config
cd src/Miningcore/bin/Debug/net6.0
dotnet Miningcore.dll -c /path/to/config.json

# Required environment variable for native libraries
export LD_LIBRARY_PATH="/home/jonathan/miningcore/build"
```

Configuration files are in `/home/jonathan/miningcore/build/`:
- `config-quai-combined.json` - Both SHA256 and Scrypt pools
- `config-quai-sha256.json` - SHA256 pool only
- `config-quai-scrypt.json` - Scrypt pool only

### Building Quai Payout Service

```bash
cd /home/jonathan/miningcore/quai-payout-service

# Download dependencies
go mod download

# Build the service
go build -o quai-payout-service ./cmd
```

### Running Quai Payout Service

```bash
cd /home/jonathan/miningcore/quai-payout-service

# Run with default config (config.yaml)
./quai-payout-service

# Run with custom config
./quai-payout-service -config /path/to/config.yaml
```

The service will prompt for the keystore password at startup if keystores are configured.

### Payout Service Configuration

Edit `config.yaml`:

```yaml
# Quai node RPC endpoint
quai_rpc: "http://localhost:9200"

# Chain ID (9000 for testnet)
chain_id: 9000

# PostgreSQL connection (must match miningcore)
postgres_conn_str: "host=localhost port=5432 user=miningcore password=password dbname=miningcore sslmode=disable"

# Encrypted keystore files for each pool
keystore_paths:
  quai-sha256: "/path/to/sha-key-encrypted.json"
  quai-scrypt: "/path/to/scrypt-key-encrypted.json"

# Starting block height (0 = start from current - 100)
start_block_height: 0

# How often to scan for rewards (seconds)
scan_interval_seconds: 10

# Confirmation depth before rewards are confirmed
confirmation_depth: 10

# Blocks until coinbase rewards unlock
coinbase_maturity: 100

# Minimum payout threshold in QUAI
min_payout_threshold: 0.1
```

### PostgreSQL Setup

```bash
# Create database and user
sudo -u postgres psql
CREATE USER miningcore WITH PASSWORD 'password';
CREATE DATABASE miningcore OWNER miningcore;
\q

# Test connection
psql -h localhost -U miningcore -d miningcore
```

### Running Both Services Together

1. Start PostgreSQL (usually auto-starts)
2. Start Quai node (must be synced and serving RPC on port 9200)
3. Start miningcore: `./build/run-debug.sh`
4. Start payout service: `cd quai-payout-service && ./quai-payout-service`

### Troubleshooting

**Miningcore can't find native libraries:**
```bash
export LD_LIBRARY_PATH="/home/jonathan/miningcore/build"
```

**PostgreSQL connection refused:**
```bash
# Check PostgreSQL is running
sudo systemctl status postgresql

# Check pg_hba.conf allows local connections
sudo nano /etc/postgresql/*/main/pg_hba.conf
# Ensure: local all all md5
sudo systemctl restart postgresql
```

**Payout service starts from block 1:**
- Set `start_block_height` in config.yaml to current block height minus ~100
- Or manually update database: `UPDATE quai_tracker_state SET last_scanned_height = <current_height>;`

**Keystore decryption fails:**
- Verify the keystore file path is correct
- Ensure the password is correct (same for all keystores)
