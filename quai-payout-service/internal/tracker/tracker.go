package tracker

import (
	"context"
	"fmt"
	"log"
	"math/big"
	"strings"
	"sync"
	"time"

	"github.com/miningcore/quai-payout-service/internal/config"
	"github.com/miningcore/quai-payout-service/internal/postgres"
	"github.com/miningcore/quai-payout-service/internal/rpc"
)

// Transaction type constants
const (
	ExternalTxType = "0x1" // ExternalTxType in go-quai
	CoinbaseType   = "0x1" // CoinbaseType ETX subtype
)

// PPLNSCalculator is a function that calculates current PPLNS scores for a specific pool
// Returns a map of miner address -> score (0.0 to 1.0)
type PPLNSCalculator func(poolID string) (map[string]float64, error)

// CoinbaseTracker scans the blockchain for coinbase rewards to pool addresses
type CoinbaseTracker struct {
	cfg               *config.Config
	rpcClient         *rpc.Client
	pgClient          *postgres.Client
	coinbaseAddresses map[string]string // lowercase address -> pool ID

	// Local cache of state for efficiency (synced with DB)
	lastScannedHeight int64
	totalRewardsFound int64
	totalValueFound   *big.Int

	mu sync.RWMutex

	// Callbacks
	onRewardFound    func(*postgres.CoinbaseReward)
	onRewardUnlocked func(*postgres.CoinbaseReward)
	pplnsCalculator  PPLNSCalculator // Called when reward is found to snapshot PPLNS scores
}

// NewCoinbaseTracker creates a new coinbase tracker
// Returns error if the same address is configured for multiple pools
func NewCoinbaseTracker(cfg *config.Config, rpcClient *rpc.Client, pgClient *postgres.Client) (*CoinbaseTracker, error) {
	// Normalize coinbase addresses to lowercase and check for duplicates
	// Map: address -> pool ID
	addresses := make(map[string]string)
	for poolID, addr := range cfg.CoinbaseAddresses {
		if addr == "" {
			continue
		}
		normalizedAddr := strings.ToLower(addr)
		if existingPoolID, exists := addresses[normalizedAddr]; exists {
			return nil, fmt.Errorf("coinbase address %s is configured for both %s and %s - each pool must have a unique address", addr, existingPoolID, poolID)
		}
		addresses[normalizedAddr] = poolID
	}

	return &CoinbaseTracker{
		cfg:               cfg,
		rpcClient:         rpcClient,
		pgClient:          pgClient,
		coinbaseAddresses: addresses,
		totalValueFound:   big.NewInt(0),
	}, nil
}

// SetOnRewardFound sets the callback for when a new reward is found
func (t *CoinbaseTracker) SetOnRewardFound(fn func(*postgres.CoinbaseReward)) {
	t.onRewardFound = fn
}

// SetOnRewardUnlocked sets the callback for when a reward unlocks
func (t *CoinbaseTracker) SetOnRewardUnlocked(fn func(*postgres.CoinbaseReward)) {
	t.onRewardUnlocked = fn
}

// SetPPLNSCalculator sets the function used to calculate PPLNS scores at reward discovery time
func (t *CoinbaseTracker) SetPPLNSCalculator(fn PPLNSCalculator) {
	t.pplnsCalculator = fn
}

// loadState loads tracker state from PostgreSQL
func (t *CoinbaseTracker) loadState() error {
	state, err := t.pgClient.GetTrackerState()
	if err != nil {
		return fmt.Errorf("failed to load tracker state: %w", err)
	}

	t.mu.Lock()
	defer t.mu.Unlock()

	t.lastScannedHeight = state.LastScannedHeight
	t.totalRewardsFound = state.TotalRewardsFound
	t.totalValueFound = state.TotalValueFound

	log.Printf("Loaded tracker state: lastScannedHeight=%d, totalRewardsFound=%d",
		t.lastScannedHeight, t.totalRewardsFound)
	return nil
}

// saveState saves tracker state to PostgreSQL
func (t *CoinbaseTracker) saveState() error {
	t.mu.RLock()
	state := &postgres.TrackerState{
		LastScannedHeight: t.lastScannedHeight,
		TotalRewardsFound: t.totalRewardsFound,
		TotalValueFound:   t.totalValueFound,
	}
	t.mu.RUnlock()

	return t.pgClient.UpdateTrackerState(state)
}

// Start begins the tracking loop
func (t *CoinbaseTracker) Start(ctx context.Context) {
	log.Printf("Starting coinbase tracker with confirmation depth %d, maturity %d blocks",
		t.cfg.ConfirmationDepth, t.cfg.CoinbaseMaturity)

	// Ensure tracker tables exist
	if err := t.pgClient.EnsureTrackerTables(); err != nil {
		log.Printf("Error creating tracker tables: %v", err)
		return
	}

	// Load previous state from database
	if err := t.loadState(); err != nil {
		log.Printf("Warning: failed to load state: %v", err)
	}

	ticker := time.NewTicker(time.Duration(t.cfg.ScanIntervalSeconds) * time.Second)
	defer ticker.Stop()

	// Initial scan
	t.scan(ctx)

	for {
		select {
		case <-ctx.Done():
			log.Printf("Coinbase tracker stopped")
			t.saveState()
			return
		case <-ticker.C:
			t.scan(ctx)
		}
	}
}

// scan scans for new coinbase rewards and checks for unlocked rewards
func (t *CoinbaseTracker) scan(ctx context.Context) {
	// Get current block height
	currentHeight, err := t.rpcClient.BlockNumber()
	if err != nil {
		log.Printf("Failed to get current block height: %v", err)
		return
	}

	// Calculate safe scan range (current - ConfirmationDepth to avoid reorgs)
	safeHeight := currentHeight - t.cfg.ConfirmationDepth
	if safeHeight < 0 {
		return
	}

	t.mu.RLock()
	lastScanned := t.lastScannedHeight
	t.mu.RUnlock()

	// If we haven't scanned before (lastScanned == 0), start from config or a reasonable default
	var startHeight int64
	if lastScanned <= 0 {
		if t.cfg.StartBlockHeight > 0 {
			// Use configured start height
			startHeight = t.cfg.StartBlockHeight
			log.Printf("Initial scan starting from configured height %d", startHeight)
		} else {
			// Default: start 100 blocks behind safe height
			startHeight = safeHeight - 100
			if startHeight < 1 {
				startHeight = 1
			}
			log.Printf("Initial scan starting from height %d (no start_block_height configured)", startHeight)
		}
	} else {
		startHeight = lastScanned + 1
	}

	// Don't scan if we're caught up
	if startHeight > safeHeight {
		// Still check for unlocked rewards
		t.checkUnlockedRewards(currentHeight)
		return
	}

	// Limit batch size to avoid overloading
	endHeight := safeHeight
	maxBatch := int64(50)
	if endHeight-startHeight > maxBatch {
		endHeight = startHeight + maxBatch
	}

	log.Printf("Scanning blocks %d to %d (current: %d, safe: %d)",
		startHeight, endHeight, currentHeight, safeHeight)

	rewardsFound := 0
	for height := startHeight; height <= endHeight; height++ {
		select {
		case <-ctx.Done():
			return
		default:
		}

		rewards, err := t.scanBlock(ctx, height)
		if err != nil {
			log.Printf("Error scanning block %d: %v", height, err)
			continue
		}

		for _, reward := range rewards {
			if err := t.processReward(reward); err != nil {
				log.Printf("Error processing reward: %v", err)
				continue
			}
			rewardsFound++
		}

		// Update last scanned height
		t.mu.Lock()
		t.lastScannedHeight = height
		t.mu.Unlock()
	}

	if rewardsFound > 0 {
		log.Printf("Found %d rewards in blocks %d-%d", rewardsFound, startHeight, endHeight)
	}

	// Check for unlocked rewards
	t.checkUnlockedRewards(currentHeight)

	// Save state periodically
	if err := t.saveState(); err != nil {
		log.Printf("Warning: failed to save state: %v", err)
	}
}

// scanBlock scans a single block's transactions for coinbase ETXs
// We look for ExternalTxType transactions with EtxType == CoinbaseType
// This is the inclusion point where the maturity period starts
func (t *CoinbaseTracker) scanBlock(ctx context.Context, height int64) ([]*postgres.CoinbaseReward, error) {
	blockData, err := t.rpcClient.GetBlockByNumber(height)
	if err != nil {
		return nil, fmt.Errorf("failed to fetch block %d: %w", height, err)
	}

	if blockData == nil {
		return nil, nil
	}

	// Use the height parameter as authoritative (from scan loop)
	// Optionally validate against block data for sanity checking
	if blockData.WoHeader.Number != "" {
		var parsedHeight int64
		numStr := strings.TrimPrefix(blockData.WoHeader.Number, "0x")
		if _, err := fmt.Sscanf(numStr, "%x", &parsedHeight); err == nil {
			if parsedHeight != height {
				log.Printf("Warning: block height mismatch at %d: RPC returned %d", height, parsedHeight)
			}
		}
	}

	var rewards []*postgres.CoinbaseReward

	for _, tx := range blockData.Transactions {
		// Check if this is an ExternalTxType transaction
		if tx.Type != ExternalTxType {
			continue
		}

		// Check if this is a CoinbaseType ETX
		if tx.EtxType != CoinbaseType {
			continue
		}

		// Check if the recipient is one of our coinbase addresses
		toAddr := strings.ToLower(tx.To)
		poolID, found := t.coinbaseAddresses[toAddr]
		if !found {
			continue
		}

		// Validate input data length - standard coinbase has exactly 33 bytes (1 lockup + 32 hash)
		// This filters out smart contract coinbases (53 or 73 bytes) and invalid transactions
		// See go-quai/core/state_processor.go RedeemLockedQuai for reference
		inputData := strings.TrimPrefix(tx.Input, "0x")
		inputLen := len(inputData) / 2 // hex string to byte count
		if inputLen != 33 {
			if inputLen == 53 || inputLen == 73 {
				// Smart contract owned coinbase - unlocked manually, skip silently
				log.Printf("Skipping smart contract owned coinbase: tx=%s", tx.Hash)
				continue
			}
			// Invalid/non-standard data length, log and skip
			log.Printf("Skipping coinbase tx with invalid data length %d bytes (expected 33): tx=%s", inputLen, tx.Hash)
			continue
		}

		// Check lockup byte - only process lockup byte 0 (standard ~2 week maturity)
		// Lockup bytes 1-3 have longer lockup periods (3/6/12 months) with reward multipliers
		// If lockup byte != 0, someone else mined this for us with a different lockup period
		// See go-quai/params/protocol_params.go LockupByteToBlockDepth
		if len(inputData) >= 2 {
			lockupByte := inputData[0:2] // First byte as hex (2 chars)
			if lockupByte != "00" {
				// Non-standard lockup period, skip silently
				log.Printf("Skipping coinbase tx with non-standard lockup byte %s: tx=%s", lockupByte, tx.Hash)
				continue
			}
		}

		// Parse the value
		value := new(big.Int)
		valueStr := strings.TrimPrefix(tx.Value, "0x")
		if valueStr == "" {
			valueStr = "0"
		}
		value.SetString(valueStr, 16)

		reward := &postgres.CoinbaseReward{
			TxHash:       tx.Hash,
			PoolID:       poolID,
			ToAddress:    tx.To,
			Value:        value,
			Algorithm:    poolID, // For backwards compatibility, use poolID as algorithm
			BlockHeight:  height,
			BlockHash:    blockData.Hash,
			UnlockHeight: height + t.cfg.CoinbaseMaturity,
			IsUnlocked:   false,
			IsPaidOut:    false,
		}

		rewards = append(rewards, reward)
	}

	return rewards, nil
}

// processReward processes a found coinbase reward
func (t *CoinbaseTracker) processReward(reward *postgres.CoinbaseReward) error {
	// Check if we already have this reward
	exists, err := t.pgClient.CoinbaseRewardExists(reward.TxHash)
	if err != nil {
		return fmt.Errorf("failed to check if reward exists: %w", err)
	}
	if exists {
		return nil
	}

	// Calculate PPLNS scores BEFORE inserting
	// Pass the pool ID so only shares from the same pool are considered
	var minerScores map[string]float64
	if t.pplnsCalculator != nil {
		scores, err := t.pplnsCalculator(reward.PoolID)
		if err != nil {
			log.Printf("Warning: failed to calculate PPLNS scores for reward (pool=%s): %v", reward.PoolID, err)
			minerScores = make(map[string]float64)
		} else {
			minerScores = scores
		}
	} else {
		minerScores = make(map[string]float64)
	}

	// Attach the PPLNS scores snapshot to the reward
	reward.MinerScores = minerScores

	// Insert into database
	if err := t.pgClient.InsertCoinbaseReward(reward); err != nil {
		return fmt.Errorf("failed to insert reward: %w", err)
	}

	// Update local statistics
	t.mu.Lock()
	t.totalRewardsFound++
	t.totalValueFound.Add(t.totalValueFound, reward.Value)
	t.mu.Unlock()

	// Convert value to human-readable format
	valueFloat := new(big.Float).SetInt(reward.Value)
	divisor := new(big.Float).SetFloat64(1e18)
	valueFloat.Quo(valueFloat, divisor)
	valueF64, _ := valueFloat.Float64()

	log.Printf("Coinbase reward included: pool=%s block=%d value=%.6f QUAI tx=%s unlocks_at=%d miners=%d",
		reward.PoolID, reward.BlockHeight, valueF64, reward.TxHash, reward.UnlockHeight, len(minerScores))

	// Notify callback
	if t.onRewardFound != nil {
		t.onRewardFound(reward)
	}

	return nil
}

// checkUnlockedRewards checks for rewards that have unlocked
func (t *CoinbaseTracker) checkUnlockedRewards(currentHeight int64) {
	// Get all pending rewards from database
	rewards, err := t.pgClient.GetPendingCoinbaseRewards()
	if err != nil {
		log.Printf("Error getting pending rewards: %v", err)
		return
	}

	for _, reward := range rewards {
		if reward.IsUnlocked || reward.IsPaidOut {
			continue
		}

		if currentHeight >= reward.UnlockHeight {
			// Mark as unlocked in database
			if err := t.pgClient.MarkCoinbaseRewardUnlocked(reward.TxHash); err != nil {
				log.Printf("Error marking reward unlocked: %v", err)
				continue
			}
			reward.IsUnlocked = true

			valueFloat := new(big.Float).SetInt(reward.Value)
			divisor := new(big.Float).SetFloat64(1e18)
			valueFloat.Quo(valueFloat, divisor)
			valueF64, _ := valueFloat.Float64()

			log.Printf("Reward unlocked: pool=%s block=%d value=%.6f QUAI tx=%s",
				reward.PoolID, reward.BlockHeight, valueF64, reward.TxHash)

			// Notify callback
			if t.onRewardUnlocked != nil {
				t.onRewardUnlocked(reward)
			}
		}
	}
}

// GetUnlockedBalance returns the total unlocked balance that hasn't been paid out
func (t *CoinbaseTracker) GetUnlockedBalance() *big.Int {
	rewards, err := t.pgClient.GetUnlockedCoinbaseRewards()
	if err != nil {
		log.Printf("Error getting unlocked rewards: %v", err)
		return big.NewInt(0)
	}

	total := big.NewInt(0)
	for _, reward := range rewards {
		total.Add(total, reward.Value)
	}
	return total
}

// GetUnlockedRewards returns all unlocked rewards that haven't been paid out
func (t *CoinbaseTracker) GetUnlockedRewards() []*postgres.CoinbaseReward {
	rewards, err := t.pgClient.GetUnlockedCoinbaseRewards()
	if err != nil {
		log.Printf("Error getting unlocked rewards: %v", err)
		return nil
	}
	return rewards
}

// MarkRewardPaidOut marks a reward as having been included in payouts
func (t *CoinbaseTracker) MarkRewardPaidOut(txHash string) {
	if err := t.pgClient.MarkCoinbaseRewardPaidOut(txHash); err != nil {
		log.Printf("Error marking reward paid out: %v", err)
	}
}

// GetStats returns tracker statistics
func (t *CoinbaseTracker) GetStats() map[string]interface{} {
	t.mu.RLock()
	lastScanned := t.lastScannedHeight
	totalFound := t.totalRewardsFound
	totalValue := t.totalValueFound.String()
	t.mu.RUnlock()

	pending, unlocked, paidOut, err := t.pgClient.GetCoinbaseRewardStats()
	if err != nil {
		log.Printf("Error getting reward stats: %v", err)
	}

	return map[string]interface{}{
		"lastScannedHeight": lastScanned,
		"totalRewardsFound": totalFound,
		"totalValueFound":   totalValue,
		"pendingRewards":    pending,
		"unlockedRewards":   unlocked,
		"paidOutRewards":    paidOut,
	}
}
