# Quai Payout Service

A Go-based sidecar service for miningcore that handles Quai Network reward tracking and miner payouts.

## Overview

This service:

1. **Tracks coinbase rewards** - Scans the Quai blockchain for coinbase ETXs (external transactions) sent to the pool's mining addresses
2. **Monitors reward maturity** - Tracks when rewards unlock based on the coinbase maturity period (100 blocks on testnet)
3. **Distributes rewards** - When rewards unlock, distributes them to miners based on PPLNS (Pay Per Last N Shares)
4. **Sends payouts** - Sends QUAI to miners when their balance exceeds the payout threshold

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                           MININGCORE                                 │
│  - Receives shares from miners                                      │
│  - Validates and stores shares in PostgreSQL                        │
│  - Records blocks found                                             │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ PostgreSQL
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    QUAI PAYOUT SERVICE                              │
│                                                                      │
│  ┌─────────────────────┐  ┌─────────────────────┐                   │
│  │ Coinbase Tracker    │  │ Payout Executor     │                   │
│  │                     │  │                     │                   │
│  │ - Scan blocks       │  │ - Read balances     │                   │
│  │ - Find rewards      │  │ - Build QuaiTx      │                   │
│  │ - Track maturity    │  │ - Sign & send       │                   │
│  │ - Distribute PPLNS  │  │ - Record payments   │                   │
│  └─────────────────────┘  └─────────────────────┘                   │
│                                                                      │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ JSON-RPC
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         QUAI NODE                                    │
└─────────────────────────────────────────────────────────────────────┘
```

## Configuration

Copy `config.yaml` and edit for your environment:

```yaml
# Pool identification (must match miningcore pool ID)
pool_id: "quai"

# Quai node RPC endpoint
quai_rpc: "http://localhost:8610"

# Chain ID (9000 for testnet)
chain_id: 9000

# PostgreSQL connection string
postgres_conn_str: "host=localhost port=5432 user=miningcore dbname=miningcore sslmode=disable"

# Coinbase addresses to track
coinbase_addresses:
  sha: "0x003EC89cE5930ef3A83788a9c9988bE36a9c33cC"
  scrypt: "0x0016DC01cADF0a2D65511B0Dbf9ABa2b4950231F"

# Confirmation depth (blocks to wait before considering reward confirmed)
confirmation_depth: 10

# Coinbase maturity (blocks until rewards unlock)
# Testnet: 100, Mainnet: ~2 weeks of blocks
coinbase_maturity: 100

# Minimum payout threshold in QUAI
min_payout_threshold: 0.1

# Pool fee (0.01 = 1%)
pool_fee_percent: 0.01
```

## Environment Variables

Create a `.env` file (see `.env.example`):

```bash
# Pool wallet private key (without 0x prefix)
POOL_PRIVATE_KEY=your_private_key_here
```

**IMPORTANT**: Keep your private key secure! Never commit it to version control.

## Building

```bash
cd quai-payout-service
go build -o quai-payout-service ./cmd/main.go
```

## Running

```bash
./quai-payout-service -config config.yaml
```

## How It Works

### 1. Reward Discovery

The service scans blocks for outbound ETXs (External Transactions) with type `0x1` (CoinbaseType) that are sent to the pool's coinbase addresses. It waits for `confirmation_depth` blocks before considering a reward confirmed to avoid issues with chain reorganizations.

### 2. Reward Maturity

Quai coinbase rewards are locked for a period before they can be spent:
- **Testnet**: 100 blocks (~8 minutes at 5s/block)
- **Mainnet**: ConversionLockPeriod (~2 weeks)

The service tracks when each reward will unlock: `unlock_height = block_height + coinbase_maturity`

### 3. PPLNS Distribution

When rewards unlock, the service distributes them to miners based on their share contributions using PPLNS (Pay Per Last N Shares):

```
score = share_difficulty / network_difficulty
miner_share = (miner_score / total_score) * reward
```

Pool fees are deducted before distribution.

### 4. Payouts

When a miner's balance exceeds the payout threshold, the service:
1. Builds a Quai transaction using the go-quai SDK
2. Signs it with the pool's private key
3. Sends it to the network
4. Records the payment in the miningcore database

## State Persistence

The service persists its state to `payout_state.json`:
- Last scanned block height
- Pending rewards (not yet unlocked)
- Reward statistics

This allows the service to resume from where it left off after a restart.

## Database Tables Used

The service reads from and writes to miningcore's PostgreSQL database:

| Table | Usage |
|-------|-------|
| `shares` | Read shares for PPLNS calculation |
| `balances` | Read/update miner balances |
| `balance_changes` | Record balance changes |
| `payments` | Record payout transactions |
| `miner_settings` | Read custom payout thresholds |

## Troubleshooting

### No rewards being found
- Check that `coinbase_addresses` match your pool's mining addresses
- Verify the Quai node is synced and accessible
- Check logs for RPC errors

### Rewards not unlocking
- Verify `coinbase_maturity` is set correctly for your network
- Check that the blockchain has progressed past the unlock height

### Payouts failing
- Ensure the pool wallet has sufficient balance for gas
- Verify the private key is correct
- Check Quai node connectivity

## License

MIT
