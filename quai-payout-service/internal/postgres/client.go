package postgres

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"log"
	"math/big"
	"sort"
	"strings"
	"time"

	"github.com/lib/pq"
)

// Client is a PostgreSQL client for miningcore database
type Client struct {
	db      *sql.DB
	poolIDs []string
	// serviceID is a unique identifier for this payout service instance (joined pool IDs)
	serviceID string
}

// NewClient creates a new PostgreSQL client for multiple pools
func NewClient(connStr string, poolIDs []string) (*Client, error) {
	db, err := sql.Open("postgres", connStr)
	if err != nil {
		return nil, fmt.Errorf("failed to open database: %w", err)
	}

	// Test connection
	if err := db.Ping(); err != nil {
		return nil, fmt.Errorf("failed to ping database: %w", err)
	}

	// Set connection pool settings
	db.SetMaxOpenConns(10)
	db.SetMaxIdleConns(5)
	db.SetConnMaxLifetime(time.Hour)

	// Create a stable service ID from sorted pool IDs
	sortedIDs := make([]string, len(poolIDs))
	copy(sortedIDs, poolIDs)
	sort.Strings(sortedIDs)
	serviceID := strings.Join(sortedIDs, "+")

	return &Client{db: db, poolIDs: poolIDs, serviceID: serviceID}, nil
}

// GetPoolIDs returns the configured pool IDs
func (c *Client) GetPoolIDs() []string {
	return c.poolIDs
}

// Close closes the database connection
func (c *Client) Close() error {
	return c.db.Close()
}

// MinerBalance represents a miner's balance for a specific pool
type MinerBalance struct {
	PoolId    string   // Pool ID where the balance was earned
	Address   string
	AmountWei *big.Int // Amount in wei for precision
}

// weiPerQuai is 10^18
var weiPerQuai = new(big.Int).Exp(big.NewInt(10), big.NewInt(18), nil)

// quaiToWeiPrecise converts QUAI (as string from DB) to wei with full precision
func quaiToWeiPrecise(quaiStr string) *big.Int {
	// Parse as big.Float for precision
	quaiFloat, _, err := big.ParseFloat(quaiStr, 10, 256, big.ToNearestEven)
	if err != nil {
		return big.NewInt(0)
	}

	// Multiply by 10^18
	weiFloat := new(big.Float).Mul(quaiFloat, new(big.Float).SetInt(weiPerQuai))

	// Convert to big.Int
	weiInt, _ := weiFloat.Int(nil)
	return weiInt
}

// weiToQuaiPrecise converts wei to QUAI string with full precision for DB storage
func weiToQuaiPrecise(wei *big.Int) string {
	if wei == nil {
		return "0"
	}

	// Use big.Rat for exact division
	rat := new(big.Rat).SetFrac(wei, weiPerQuai)

	// Format with enough precision (18 decimal places for wei precision)
	return rat.FloatString(18)
}

// GetMinerBalances returns all miner balances above the threshold (per-pool, not aggregated)
// threshold is in QUAI
func (c *Client) GetMinerBalances(minThresholdQuai float64) ([]MinerBalance, error) {
	// Return per-pool balances so we can pay from the correct wallet
	query := `
		SELECT poolid, address, amount::text
		FROM balances
		WHERE poolid = ANY($1) AND amount >= $2
		ORDER BY amount DESC
	`

	rows, err := c.db.Query(query, pq.Array(c.poolIDs), minThresholdQuai)
	if err != nil {
		return nil, fmt.Errorf("failed to query balances: %w", err)
	}
	defer rows.Close()

	var balances []MinerBalance
	for rows.Next() {
		var poolId, address, amountStr string
		if err := rows.Scan(&poolId, &address, &amountStr); err != nil {
			return nil, fmt.Errorf("failed to scan balance row: %w", err)
		}
		balances = append(balances, MinerBalance{
			PoolId:    poolId,
			Address:   address,
			AmountWei: quaiToWeiPrecise(amountStr),
		})
	}

	return balances, rows.Err()
}

// GetMinerBalance returns a single miner's total balance in wei (aggregated across all pools)
func (c *Client) GetMinerBalance(address string) (*big.Int, error) {
	query := `SELECT COALESCE(SUM(amount), 0)::text FROM balances WHERE poolid = ANY($1) AND address = $2`

	var amountStr string
	err := c.db.QueryRow(query, pq.Array(c.poolIDs), address).Scan(&amountStr)
	if err != nil {
		return nil, fmt.Errorf("failed to query balance: %w", err)
	}

	return quaiToWeiPrecise(amountStr), nil
}

// GetMinerPayoutThreshold returns a miner's custom payout threshold, or 0 if not set
// Checks across all configured pools and returns the first found threshold
func (c *Client) GetMinerPayoutThreshold(address string) (float64, error) {
	query := `SELECT paymentthreshold FROM miner_settings WHERE poolid = ANY($1) AND address = $2 LIMIT 1`

	var threshold float64
	err := c.db.QueryRow(query, pq.Array(c.poolIDs), address).Scan(&threshold)
	if err == sql.ErrNoRows {
		return 0, nil
	}
	if err != nil {
		return 0, fmt.Errorf("failed to query threshold: %w", err)
	}

	return threshold, nil
}

// DeductBalance deducts an amount (in wei) from a miner's balance for a specific pool
func (c *Client) DeductBalance(poolID, address string, amountWei *big.Int) error {
	tx, err := c.db.Begin()
	if err != nil {
		return fmt.Errorf("failed to begin transaction: %w", err)
	}
	defer tx.Rollback()

	// Convert wei to QUAI string for DB
	amountQuai := weiToQuaiPrecise(amountWei)

	// Update balance
	updateQuery := `
		UPDATE balances
		SET amount = amount - $1::numeric, updated = NOW()
		WHERE poolid = $2 AND address = $3
	`
	result, err := tx.Exec(updateQuery, amountQuai, poolID, address)
	if err != nil {
		return fmt.Errorf("failed to update balance: %w", err)
	}

	rowsAffected, err := result.RowsAffected()
	if err != nil {
		return fmt.Errorf("failed to get rows affected: %w", err)
	}
	if rowsAffected == 0 {
		return fmt.Errorf("no balance found for address %s in pool %s", address, poolID)
	}

	// Record balance change (negative amount for deduction)
	negAmountQuai := weiToQuaiPrecise(new(big.Int).Neg(amountWei))
	changeQuery := `
		INSERT INTO balance_changes (poolid, address, amount, usage, created)
		VALUES ($1, $2, $3::numeric, 'payment', NOW())
	`
	_, err = tx.Exec(changeQuery, poolID, address, negAmountQuai)
	if err != nil {
		return fmt.Errorf("failed to record balance change: %w", err)
	}

	return tx.Commit()
}

// RecordPayment records a payment in the payments table (amount in wei)
func (c *Client) RecordPayment(poolID, address, coin, txHash string, amountWei *big.Int) error {
	amountQuai := weiToQuaiPrecise(amountWei)

	query := `
		INSERT INTO payments (poolid, coin, address, amount, transactionconfirmationdata, created)
		VALUES ($1, $2, $3, $4::numeric, $5, NOW())
	`

	_, err := c.db.Exec(query, poolID, coin, address, amountQuai, txHash)
	if err != nil {
		return fmt.Errorf("failed to record payment: %w", err)
	}

	log.Printf("Recorded payment: pool=%s address=%s amount=%s QUAI txHash=%s", poolID, address, amountQuai, txHash)
	return nil
}

// GetPendingBlocks returns blocks that are pending confirmation (across all configured pools)
func (c *Client) GetPendingBlocks() ([]Block, error) {
	query := `
		SELECT id, poolid, blockheight, status, COALESCE(type, ''), reward, hash,
		       COALESCE(transactionconfirmationdata, ''), created
		FROM blocks
		WHERE poolid = ANY($1) AND status = 'pending'
		ORDER BY blockheight ASC
	`

	rows, err := c.db.Query(query, pq.Array(c.poolIDs))
	if err != nil {
		return nil, fmt.Errorf("failed to query blocks: %w", err)
	}
	defer rows.Close()

	var blocks []Block
	for rows.Next() {
		var b Block
		var reward sql.NullFloat64
		var hash sql.NullString
		if err := rows.Scan(&b.ID, &b.PoolID, &b.BlockHeight, &b.Status, &b.Type, &reward, &hash, &b.WorkshareHash, &b.Created); err != nil {
			return nil, fmt.Errorf("failed to scan block row: %w", err)
		}
		if reward.Valid {
			b.Reward = reward.Float64
		}
		if hash.Valid {
			b.Hash = hash.String
		}
		blocks = append(blocks, b)
	}

	return blocks, rows.Err()
}

// Block represents a block record
type Block struct {
	ID            int64
	PoolID        string
	BlockHeight   int64
	Status        string
	Type          string // Algorithm type: "sha" or "scrypt"
	Reward        float64
	Hash          string
	WorkshareHash string // Stored in transactionconfirmationdata - the workshare hash from node
	Created       time.Time
}

// BlockLookupResult contains the pool ID and algorithm for a found block
type BlockLookupResult struct {
	PoolID    string
	Algorithm string // "sha" or "scrypt"
}

// LookupBlockByWorkshareHash looks up a block by its workshare hash (stored in transactionconfirmationdata)
// Returns nil if no matching block is found
func (c *Client) LookupBlockByWorkshareHash(workshareHash string) (*BlockLookupResult, error) {
	query := `
		SELECT poolid, COALESCE(type, '') as type
		FROM blocks
		WHERE transactionconfirmationdata = $1
		LIMIT 1
	`

	var result BlockLookupResult
	err := c.db.QueryRow(query, workshareHash).Scan(&result.PoolID, &result.Algorithm)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("failed to lookup block by workshare hash: %w", err)
	}

	return &result, nil
}

// UpdateBlockStatus updates the status of a block
func (c *Client) UpdateBlockStatus(blockID int64, status string, reward float64) error {
	query := `UPDATE blocks SET status = $1, reward = $2 WHERE id = $3`

	_, err := c.db.Exec(query, status, reward, blockID)
	if err != nil {
		return fmt.Errorf("failed to update block status: %w", err)
	}

	return nil
}

// UpdateBlockStatusByWorkshareHash updates the status of a block using its workshare hash
func (c *Client) UpdateBlockStatusByWorkshareHash(workshareHash, status string, reward float64) error {
	query := `UPDATE blocks SET status = $1, reward = $2 WHERE transactionconfirmationdata = $3`

	result, err := c.db.Exec(query, status, reward, workshareHash)
	if err != nil {
		return fmt.Errorf("failed to update block status: %w", err)
	}

	rowsAffected, _ := result.RowsAffected()
	if rowsAffected == 0 {
		log.Printf("Warning: no block found with workshare hash %s to update", workshareHash)
	}

	return nil
}

// AddBalance adds to a miner's balance for a specific pool (amount in wei, used when distributing block rewards)
func (c *Client) AddBalance(poolID, address string, amountWei *big.Int, usage string) error {
	tx, err := c.db.Begin()
	if err != nil {
		return fmt.Errorf("failed to begin transaction: %w", err)
	}
	defer tx.Rollback()

	// Convert wei to QUAI string for DB
	amountQuai := weiToQuaiPrecise(amountWei)

	// Upsert balance
	upsertQuery := `
		INSERT INTO balances (poolid, address, amount, created, updated)
		VALUES ($1, $2, $3::numeric, NOW(), NOW())
		ON CONFLICT (poolid, address) DO UPDATE
		SET amount = balances.amount + $3::numeric, updated = NOW()
	`
	_, err = tx.Exec(upsertQuery, poolID, address, amountQuai)
	if err != nil {
		return fmt.Errorf("failed to update balance: %w", err)
	}

	// Record balance change
	changeQuery := `
		INSERT INTO balance_changes (poolid, address, amount, usage, created)
		VALUES ($1, $2, $3::numeric, $4, NOW())
	`
	_, err = tx.Exec(changeQuery, poolID, address, amountQuai, usage)
	if err != nil {
		return fmt.Errorf("failed to record balance change: %w", err)
	}

	return tx.Commit()
}

// GetRecentShares returns recent shares for PPLNS calculation for a specific pool
// The poolID matches the shares.poolid column (e.g., "quai-sha256", "quai-scrypt")
func (c *Client) GetRecentShares(window time.Duration, poolID string) ([]Share, error) {
	cutoff := time.Now().Add(-window)

	query := `
		SELECT miner, difficulty, networkdifficulty, created
		FROM shares
		WHERE poolid = $1 AND created >= $2
		ORDER BY created DESC
	`

	rows, err := c.db.Query(query, poolID, cutoff)
	if err != nil {
		return nil, fmt.Errorf("failed to query shares: %w", err)
	}
	defer rows.Close()

	var shares []Share
	for rows.Next() {
		var s Share
		if err := rows.Scan(&s.Miner, &s.Difficulty, &s.NetworkDifficulty, &s.Created); err != nil {
			return nil, fmt.Errorf("failed to scan share row: %w", err)
		}
		shares = append(shares, s)
	}

	return shares, rows.Err()
}

// Share represents a share record
type Share struct {
	Miner             string
	Difficulty        float64
	NetworkDifficulty float64
	Created           time.Time
}

// CalculatePPLNSScores calculates PPLNS scores for miners on a specific pool
// Returns a map of miner address -> score (0.0 to 1.0, representing percentage of reward)
func (c *Client) CalculatePPLNSScores(window time.Duration, poolID string) (map[string]float64, error) {
	shares, err := c.GetRecentShares(window, poolID)
	if err != nil {
		return nil, err
	}

	if len(shares) == 0 {
		return map[string]float64{}, nil
	}

	// Calculate scores: score = difficulty / networkDifficulty
	minerScores := make(map[string]*big.Float)
	totalScore := new(big.Float)

	for _, share := range shares {
		score := share.Difficulty / share.NetworkDifficulty
		scoreBig := new(big.Float).SetFloat64(score)

		if minerScores[share.Miner] == nil {
			minerScores[share.Miner] = new(big.Float)
		}
		minerScores[share.Miner].Add(minerScores[share.Miner], scoreBig)
		totalScore.Add(totalScore, scoreBig)
	}

	// Normalize to percentages
	result := make(map[string]float64)
	for miner, score := range minerScores {
		percentage := new(big.Float).Quo(score, totalScore)
		pct, _ := percentage.Float64()
		result[miner] = pct
	}

	return result, nil
}

// GetTotalBalance returns the sum of all miner balances in wei (across all configured pools)
func (c *Client) GetTotalBalance() (*big.Int, error) {
	query := `SELECT COALESCE(SUM(amount), 0)::text FROM balances WHERE poolid = ANY($1)`

	var totalStr string
	if err := c.db.QueryRow(query, pq.Array(c.poolIDs)).Scan(&totalStr); err != nil {
		return nil, fmt.Errorf("failed to query total balance: %w", err)
	}

	return quaiToWeiPrecise(totalStr), nil
}

// ============================================================================
// Tracker State Tables (for coinbase reward tracking)
// ============================================================================

// EnsureTrackerTables creates the tracker state tables if they don't exist
func (c *Client) EnsureTrackerTables() error {
	// Table for tracker state (scan position, statistics)
	stateTableQuery := `
		CREATE TABLE IF NOT EXISTS payout_tracker_state (
			poolid TEXT PRIMARY KEY,
			last_scanned_height BIGINT NOT NULL DEFAULT 0,
			total_rewards_found BIGINT NOT NULL DEFAULT 0,
			total_value_found NUMERIC(38,0) NOT NULL DEFAULT 0,
			updated TIMESTAMPTZ NOT NULL DEFAULT NOW()
		)
	`
	if _, err := c.db.Exec(stateTableQuery); err != nil {
		return fmt.Errorf("failed to create payout_tracker_state table: %w", err)
	}

	// Table for pending coinbase rewards
	// workshare_hash is the unique identifier (from block submission)
	// tx_hash is the coinbase transaction hash (discovered later via API or blockchain scan)
	rewardsTableQuery := `
		CREATE TABLE IF NOT EXISTS pending_coinbase_rewards (
			workshare_hash TEXT PRIMARY KEY,
			tx_hash TEXT,
			poolid TEXT NOT NULL,
			to_address TEXT NOT NULL,
			value NUMERIC(38,0) NOT NULL,
			algorithm TEXT NOT NULL,
			block_height BIGINT NOT NULL,
			block_hash TEXT NOT NULL,
			unlock_height BIGINT NOT NULL,
			is_unlocked BOOLEAN NOT NULL DEFAULT FALSE,
			is_paid_out BOOLEAN NOT NULL DEFAULT FALSE,
			miner_scores JSONB,
			created TIMESTAMPTZ NOT NULL DEFAULT NOW()
		)
	`
	if _, err := c.db.Exec(rewardsTableQuery); err != nil {
		return fmt.Errorf("failed to create pending_coinbase_rewards table: %w", err)
	}

	// Create index for efficient queries
	indexQuery := `
		CREATE INDEX IF NOT EXISTS idx_pending_coinbase_rewards_poolid
		ON pending_coinbase_rewards(poolid, is_unlocked, is_paid_out)
	`
	if _, err := c.db.Exec(indexQuery); err != nil {
		return fmt.Errorf("failed to create index: %w", err)
	}

	return nil
}

// TrackerState holds the tracker's scan position and statistics
type TrackerState struct {
	LastScannedHeight int64
	TotalRewardsFound int64
	TotalValueFound   *big.Int
}

// GetTrackerState retrieves the current tracker state for this service instance
func (c *Client) GetTrackerState() (*TrackerState, error) {
	query := `
		SELECT last_scanned_height, total_rewards_found, total_value_found::text
		FROM payout_tracker_state
		WHERE poolid = $1
	`

	var state TrackerState
	var totalValueStr string

	// Use serviceID as the key (represents this multi-pool service instance)
	err := c.db.QueryRow(query, c.serviceID).Scan(
		&state.LastScannedHeight,
		&state.TotalRewardsFound,
		&totalValueStr,
	)

	if err == sql.ErrNoRows {
		// Return default state
		return &TrackerState{
			LastScannedHeight: 0,
			TotalRewardsFound: 0,
			TotalValueFound:   big.NewInt(0),
		}, nil
	}
	if err != nil {
		return nil, fmt.Errorf("failed to get tracker state: %w", err)
	}

	state.TotalValueFound = new(big.Int)
	state.TotalValueFound.SetString(totalValueStr, 10)

	return &state, nil
}

// UpdateTrackerState updates the tracker state for this service instance
func (c *Client) UpdateTrackerState(state *TrackerState) error {
	query := `
		INSERT INTO payout_tracker_state (poolid, last_scanned_height, total_rewards_found, total_value_found, updated)
		VALUES ($1, $2, $3, $4, NOW())
		ON CONFLICT (poolid) DO UPDATE
		SET last_scanned_height = $2, total_rewards_found = $3, total_value_found = $4, updated = NOW()
	`

	totalValueStr := "0"
	if state.TotalValueFound != nil {
		totalValueStr = state.TotalValueFound.String()
	}

	// Use serviceID as the key (represents this multi-pool service instance)
	_, err := c.db.Exec(query, c.serviceID, state.LastScannedHeight, state.TotalRewardsFound, totalValueStr)
	if err != nil {
		return fmt.Errorf("failed to update tracker state: %w", err)
	}

	return nil
}

// CoinbaseReward represents a tracked coinbase reward
type CoinbaseReward struct {
	WorkshareHash string   // Unique identifier - the workshare hash from block submission
	TxHash        string   // Coinbase transaction hash (may be empty until discovered)
	PoolID        string   // Which pool this reward belongs to
	ToAddress     string
	Value         *big.Int
	Algorithm     string
	BlockHeight   int64
	BlockHash     string
	UnlockHeight  int64
	IsUnlocked    bool
	IsPaidOut     bool
	MinerScores   map[string]float64
	Created       time.Time
}

// InsertCoinbaseReward inserts a new coinbase reward
// The reward.PoolID and reward.WorkshareHash fields must be set
func (c *Client) InsertCoinbaseReward(reward *CoinbaseReward) error {
	// Serialize miner scores to JSON
	var minerScoresJSON []byte
	var err error
	if reward.MinerScores != nil {
		minerScoresJSON, err = json.Marshal(reward.MinerScores)
		if err != nil {
			return fmt.Errorf("failed to marshal miner scores: %w", err)
		}
	}

	query := `
		INSERT INTO pending_coinbase_rewards
		(workshare_hash, tx_hash, poolid, to_address, value, algorithm, block_height, block_hash, unlock_height, is_unlocked, is_paid_out, miner_scores, created)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, NOW())
		ON CONFLICT (workshare_hash) DO NOTHING
	`

	valueStr := "0"
	if reward.Value != nil {
		valueStr = reward.Value.String()
	}

	_, err = c.db.Exec(query,
		reward.WorkshareHash,
		reward.TxHash, // May be empty if not yet discovered
		reward.PoolID,
		reward.ToAddress,
		valueStr,
		reward.Algorithm,
		reward.BlockHeight,
		reward.BlockHash,
		reward.UnlockHeight,
		reward.IsUnlocked,
		reward.IsPaidOut,
		minerScoresJSON,
	)
	if err != nil {
		return fmt.Errorf("failed to insert coinbase reward: %w", err)
	}

	return nil
}

// GetPendingCoinbaseRewards returns all pending (not paid out) coinbase rewards across all configured pools
func (c *Client) GetPendingCoinbaseRewards() ([]*CoinbaseReward, error) {
	query := `
		SELECT workshare_hash, COALESCE(tx_hash, ''), poolid, to_address, value::text, algorithm, block_height, block_hash,
		       unlock_height, is_unlocked, is_paid_out, miner_scores, created
		FROM pending_coinbase_rewards
		WHERE poolid = ANY($1) AND is_paid_out = FALSE
		ORDER BY block_height ASC
	`

	rows, err := c.db.Query(query, pq.Array(c.poolIDs))
	if err != nil {
		return nil, fmt.Errorf("failed to query coinbase rewards: %w", err)
	}
	defer rows.Close()

	var rewards []*CoinbaseReward
	for rows.Next() {
		var r CoinbaseReward
		var valueStr string
		var minerScoresJSON []byte

		if err := rows.Scan(
			&r.WorkshareHash,
			&r.TxHash,
			&r.PoolID,
			&r.ToAddress,
			&valueStr,
			&r.Algorithm,
			&r.BlockHeight,
			&r.BlockHash,
			&r.UnlockHeight,
			&r.IsUnlocked,
			&r.IsPaidOut,
			&minerScoresJSON,
			&r.Created,
		); err != nil {
			return nil, fmt.Errorf("failed to scan coinbase reward: %w", err)
		}

		r.Value = new(big.Int)
		r.Value.SetString(valueStr, 10)

		if len(minerScoresJSON) > 0 {
			if err := json.Unmarshal(minerScoresJSON, &r.MinerScores); err != nil {
				log.Printf("Warning: failed to unmarshal miner scores for %s: %v", r.WorkshareHash, err)
				r.MinerScores = make(map[string]float64)
			}
		} else {
			r.MinerScores = make(map[string]float64)
		}

		rewards = append(rewards, &r)
	}

	return rewards, rows.Err()
}

// GetUnlockedCoinbaseRewards returns unlocked rewards that haven't been paid out (across all configured pools)
func (c *Client) GetUnlockedCoinbaseRewards() ([]*CoinbaseReward, error) {
	query := `
		SELECT workshare_hash, COALESCE(tx_hash, ''), poolid, to_address, value::text, algorithm, block_height, block_hash,
		       unlock_height, is_unlocked, is_paid_out, miner_scores, created
		FROM pending_coinbase_rewards
		WHERE poolid = ANY($1) AND is_unlocked = TRUE AND is_paid_out = FALSE
		ORDER BY block_height ASC
	`

	rows, err := c.db.Query(query, pq.Array(c.poolIDs))
	if err != nil {
		return nil, fmt.Errorf("failed to query unlocked rewards: %w", err)
	}
	defer rows.Close()

	var rewards []*CoinbaseReward
	for rows.Next() {
		var r CoinbaseReward
		var valueStr string
		var minerScoresJSON []byte

		if err := rows.Scan(
			&r.WorkshareHash,
			&r.TxHash,
			&r.PoolID,
			&r.ToAddress,
			&valueStr,
			&r.Algorithm,
			&r.BlockHeight,
			&r.BlockHash,
			&r.UnlockHeight,
			&r.IsUnlocked,
			&r.IsPaidOut,
			&minerScoresJSON,
			&r.Created,
		); err != nil {
			return nil, fmt.Errorf("failed to scan coinbase reward: %w", err)
		}

		r.Value = new(big.Int)
		r.Value.SetString(valueStr, 10)

		if len(minerScoresJSON) > 0 {
			if err := json.Unmarshal(minerScoresJSON, &r.MinerScores); err != nil {
				log.Printf("Warning: failed to unmarshal miner scores for %s: %v", r.WorkshareHash, err)
				r.MinerScores = make(map[string]float64)
			}
		} else {
			r.MinerScores = make(map[string]float64)
		}

		rewards = append(rewards, &r)
	}

	return rewards, rows.Err()
}

// MarkCoinbaseRewardUnlocked marks a reward as unlocked (by workshare hash)
func (c *Client) MarkCoinbaseRewardUnlocked(workshareHash string) error {
	query := `UPDATE pending_coinbase_rewards SET is_unlocked = TRUE WHERE workshare_hash = $1`
	_, err := c.db.Exec(query, workshareHash)
	if err != nil {
		return fmt.Errorf("failed to mark reward unlocked: %w", err)
	}
	return nil
}

// MarkCoinbaseRewardPaidOut marks a reward as paid out (by workshare hash)
func (c *Client) MarkCoinbaseRewardPaidOut(workshareHash string) error {
	query := `UPDATE pending_coinbase_rewards SET is_paid_out = TRUE WHERE workshare_hash = $1`
	_, err := c.db.Exec(query, workshareHash)
	if err != nil {
		return fmt.Errorf("failed to mark reward paid out: %w", err)
	}
	return nil
}

// RewardDistribution represents a single miner's share of a reward
type RewardDistribution struct {
	Address   string
	AmountWei *big.Int
	Usage     string // "block_reward" or "pool_fee"
}

// DistributeRewardAtomically atomically marks a reward as paid out and distributes balances to miners.
// This prevents double-distribution even with multiple service instances or failures.
// Returns (distributed bool, error) - distributed=false means reward was already paid out.
func (c *Client) DistributeRewardAtomically(workshareHash, poolID string, distributions []RewardDistribution) (bool, error) {
	tx, err := c.db.Begin()
	if err != nil {
		return false, fmt.Errorf("failed to begin transaction: %w", err)
	}
	defer tx.Rollback()

	// Lock the reward row and fetch state for validation
	// FOR UPDATE ensures only one process can distribute this reward
	var dbPoolID string
	var isUnlocked, isPaidOut bool
	lockQuery := `
		SELECT poolid, is_unlocked, is_paid_out FROM pending_coinbase_rewards
		WHERE workshare_hash = $1
		FOR UPDATE
	`
	if err := tx.QueryRow(lockQuery, workshareHash).Scan(&dbPoolID, &isUnlocked, &isPaidOut); err != nil {
		if err == sql.ErrNoRows {
			return false, fmt.Errorf("reward %s not found", workshareHash)
		}
		return false, fmt.Errorf("failed to lock reward: %w", err)
	}

	// Already distributed - this is idempotent, not an error
	if isPaidOut {
		return false, nil
	}

	// Sanity check: verify poolID matches
	if dbPoolID != poolID {
		return false, fmt.Errorf("pool ID mismatch for reward %s: expected %s, got %s", workshareHash, poolID, dbPoolID)
	}

	// Sanity check: verify reward is actually unlocked
	if !isUnlocked {
		return false, fmt.Errorf("reward %s is not yet unlocked", workshareHash)
	}

	// Mark as paid out FIRST (within same transaction)
	// This ensures if we crash after this point, we won't re-distribute
	markQuery := `UPDATE pending_coinbase_rewards SET is_paid_out = TRUE WHERE workshare_hash = $1`
	if _, err := tx.Exec(markQuery, workshareHash); err != nil {
		return false, fmt.Errorf("failed to mark reward paid out: %w", err)
	}

	// Distribute balances to all miners
	for _, dist := range distributions {
		amountQuai := weiToQuaiPrecise(dist.AmountWei)

		// Upsert balance
		upsertQuery := `
			INSERT INTO balances (poolid, address, amount, created, updated)
			VALUES ($1, $2, $3::numeric, NOW(), NOW())
			ON CONFLICT (poolid, address) DO UPDATE
			SET amount = balances.amount + $3::numeric, updated = NOW()
		`
		if _, err := tx.Exec(upsertQuery, poolID, dist.Address, amountQuai); err != nil {
			return false, fmt.Errorf("failed to update balance for %s: %w", dist.Address, err)
		}

		// Record balance change with workshare hash as reference for auditability
		changeQuery := `
			INSERT INTO balance_changes (poolid, address, amount, usage, created)
			VALUES ($1, $2, $3::numeric, $4, NOW())
		`
		usageWithRef := fmt.Sprintf("%s:%s", dist.Usage, workshareHash)
		if _, err := tx.Exec(changeQuery, poolID, dist.Address, amountQuai, usageWithRef); err != nil {
			return false, fmt.Errorf("failed to record balance change for %s: %w", dist.Address, err)
		}
	}

	if err := tx.Commit(); err != nil {
		return false, fmt.Errorf("failed to commit distribution: %w", err)
	}

	return true, nil
}

// CoinbaseRewardExists checks if a reward with the given workshare hash already exists
func (c *Client) CoinbaseRewardExists(workshareHash string) (bool, error) {
	query := `SELECT EXISTS(SELECT 1 FROM pending_coinbase_rewards WHERE workshare_hash = $1)`
	var exists bool
	err := c.db.QueryRow(query, workshareHash).Scan(&exists)
	if err != nil {
		return false, fmt.Errorf("failed to check reward exists: %w", err)
	}
	return exists, nil
}

// GetCoinbaseRewardStats returns statistics about coinbase rewards (across all configured pools)
func (c *Client) GetCoinbaseRewardStats() (pending, unlocked, paidOut int, err error) {
	query := `
		SELECT
			COUNT(*) FILTER (WHERE is_unlocked = FALSE AND is_paid_out = FALSE) as pending,
			COUNT(*) FILTER (WHERE is_unlocked = TRUE AND is_paid_out = FALSE) as unlocked,
			COUNT(*) FILTER (WHERE is_paid_out = TRUE) as paid_out
		FROM pending_coinbase_rewards
		WHERE poolid = ANY($1)
	`
	err = c.db.QueryRow(query, pq.Array(c.poolIDs)).Scan(&pending, &unlocked, &paidOut)
	if err != nil {
		return 0, 0, 0, fmt.Errorf("failed to get reward stats: %w", err)
	}
	return pending, unlocked, paidOut, nil
}

// ============================================================================
// Pending Payments (for atomic payout processing)
// ============================================================================

// PendingPayment represents a payment that has been initiated but not yet confirmed
type PendingPayment struct {
	ID         int64
	Address    string
	AmountWei  *big.Int
	TxHash     string
	Status     string // "pending", "confirmed", "failed"
	Deductions map[string]string // pool_id -> amount deducted (as QUAI string for precision)
	Created    time.Time
	Updated    time.Time
}

// EnsurePendingPaymentsTable creates the pending_payments table if it doesn't exist
func (c *Client) EnsurePendingPaymentsTable() error {
	query := `
		CREATE TABLE IF NOT EXISTS pending_payments (
			id SERIAL PRIMARY KEY,
			service_id TEXT NOT NULL,
			address TEXT NOT NULL,
			amount NUMERIC(38,18) NOT NULL,
			tx_hash TEXT,
			status TEXT NOT NULL DEFAULT 'pending',
			deductions JSONB,
			created TIMESTAMPTZ NOT NULL DEFAULT NOW(),
			updated TIMESTAMPTZ NOT NULL DEFAULT NOW()
		)
	`
	if _, err := c.db.Exec(query); err != nil {
		return fmt.Errorf("failed to create pending_payments table: %w", err)
	}

	// Index for finding pending payments
	indexQuery := `
		CREATE INDEX IF NOT EXISTS idx_pending_payments_status
		ON pending_payments(service_id, status, address)
	`
	if _, err := c.db.Exec(indexQuery); err != nil {
		return fmt.Errorf("failed to create pending_payments index: %w", err)
	}

	return nil
}

// CreatePendingPayment atomically creates a pending payment record and deducts from a specific pool's balance.
// This ensures the balance is reserved before any blockchain transaction is broadcast.
// Returns the pending payment ID for later confirmation/failure handling.
func (c *Client) CreatePendingPayment(poolID string, address string, amountWei *big.Int) (int64, error) {
	tx, err := c.db.Begin()
	if err != nil {
		return 0, fmt.Errorf("failed to begin transaction: %w", err)
	}
	defer tx.Rollback()

	// Note: We allow multiple pending payments per address because:
	// 1. Balance is deducted atomically when each pending payment is created
	// 2. Each payment only uses balance available at creation time
	// 3. If a payment fails, balance is restored
	// This allows continuous payouts even if one transaction is stuck

	// Get balance for this address from the specific pool
	var balanceStr string
	balanceQuery := `SELECT COALESCE(amount, 0)::text FROM balances WHERE poolid = $1 AND address = $2`
	if err := tx.QueryRow(balanceQuery, poolID, address).Scan(&balanceStr); err != nil {
		return 0, fmt.Errorf("failed to query balance: %w", err)
	}

	availableWei := quaiToWeiPrecise(balanceStr)

	// Check if we have enough balance in this pool
	if availableWei.Cmp(amountWei) < 0 {
		return 0, fmt.Errorf("insufficient balance in pool %s for address %s (have %s wei, need %s wei)",
			poolID, address, availableWei.String(), amountWei.String())
	}

	// Deduct from this pool's balance
	deductQuai := weiToQuaiPrecise(amountWei)
	negDeductQuai := weiToQuaiPrecise(new(big.Int).Neg(amountWei))

	updateQuery := `
		UPDATE balances
		SET amount = amount - $1::numeric, updated = NOW()
		WHERE poolid = $2 AND address = $3
	`
	if _, err := tx.Exec(updateQuery, deductQuai, poolID, address); err != nil {
		return 0, fmt.Errorf("failed to deduct balance from pool %s: %w", poolID, err)
	}

	// Record the balance change
	changeQuery := `
		INSERT INTO balance_changes (poolid, address, amount, usage, created)
		VALUES ($1, $2, $3::numeric, 'payment_pending', NOW())
	`
	if _, err := tx.Exec(changeQuery, poolID, address, negDeductQuai); err != nil {
		return 0, fmt.Errorf("failed to record balance change for pool %s: %w", poolID, err)
	}

	// Record deductions (single pool now)
	deductions := map[string]string{poolID: deductQuai}
	deductionsJSON, err := json.Marshal(deductions)
	if err != nil {
		return 0, fmt.Errorf("failed to marshal deductions: %w", err)
	}

	// Create the pending payment record
	amountQuai := weiToQuaiPrecise(amountWei)
	var paymentID int64
	insertQuery := `
		INSERT INTO pending_payments (service_id, address, amount, status, deductions, created, updated)
		VALUES ($1, $2, $3::numeric, 'pending', $4, NOW(), NOW())
		RETURNING id
	`
	if err := tx.QueryRow(insertQuery, c.serviceID, address, amountQuai, deductionsJSON).Scan(&paymentID); err != nil {
		return 0, fmt.Errorf("failed to create pending payment: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return 0, fmt.Errorf("failed to commit transaction: %w", err)
	}

	return paymentID, nil
}

// SetPendingPaymentTxHash updates the tx hash for a pending payment (called after broadcast)
func (c *Client) SetPendingPaymentTxHash(paymentID int64, txHash string) error {
	query := `UPDATE pending_payments SET tx_hash = $1, updated = NOW() WHERE id = $2`
	_, err := c.db.Exec(query, txHash, paymentID)
	if err != nil {
		return fmt.Errorf("failed to set tx hash: %w", err)
	}
	return nil
}

// ConfirmPendingPayment marks a pending payment as confirmed and records it in the payments table.
// Called after the blockchain transaction is confirmed with a successful receipt.
func (c *Client) ConfirmPendingPayment(paymentID int64, txHash string) error {
	tx, err := c.db.Begin()
	if err != nil {
		return fmt.Errorf("failed to begin transaction: %w", err)
	}
	defer tx.Rollback()

	// Get the pending payment details including deductions to determine pool ID
	var address, amountStr string
	var deductionsJSON []byte
	query := `SELECT address, amount::text, deductions FROM pending_payments WHERE id = $1 AND status = 'pending'`
	if err := tx.QueryRow(query, paymentID).Scan(&address, &amountStr, &deductionsJSON); err != nil {
		if err == sql.ErrNoRows {
			return fmt.Errorf("pending payment %d not found or already processed", paymentID)
		}
		return fmt.Errorf("failed to get pending payment: %w", err)
	}

	// Extract pool ID from deductions (map of poolID -> amount)
	poolIDForRecord := c.poolIDs[0] // fallback
	if len(deductionsJSON) > 0 {
		var deductions map[string]string
		if err := json.Unmarshal(deductionsJSON, &deductions); err == nil {
			// Use the first (and typically only) pool ID from deductions
			for poolID := range deductions {
				poolIDForRecord = poolID
				break
			}
		}
	}

	// Mark as confirmed
	updateQuery := `UPDATE pending_payments SET status = 'confirmed', tx_hash = $1, updated = NOW() WHERE id = $2`
	if _, err := tx.Exec(updateQuery, txHash, paymentID); err != nil {
		return fmt.Errorf("failed to confirm pending payment: %w", err)
	}

	// Record the payment in the payments table with the correct pool ID
	insertQuery := `
		INSERT INTO payments (poolid, coin, address, amount, transactionconfirmationdata, created)
		VALUES ($1, 'QUAI', $2, $3::numeric, $4, NOW())
	`
	if _, err := tx.Exec(insertQuery, poolIDForRecord, address, amountStr, txHash); err != nil {
		return fmt.Errorf("failed to record payment: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return fmt.Errorf("failed to commit transaction: %w", err)
	}

	log.Printf("Payment confirmed: address=%s amount=%s QUAI txHash=%s", address, amountStr, txHash)
	return nil
}

// FailPendingPayment marks a payment as failed and restores the miner's balance.
// Called when the blockchain transaction fails or is dropped.
func (c *Client) FailPendingPayment(paymentID int64, reason string) error {
	tx, err := c.db.Begin()
	if err != nil {
		return fmt.Errorf("failed to begin transaction: %w", err)
	}
	defer tx.Rollback()

	// Get the pending payment details including deductions
	var address, amountStr string
	var deductionsJSON []byte
	query := `SELECT address, amount::text, deductions FROM pending_payments WHERE id = $1 AND status = 'pending'`
	if err := tx.QueryRow(query, paymentID).Scan(&address, &amountStr, &deductionsJSON); err != nil {
		if err == sql.ErrNoRows {
			return fmt.Errorf("pending payment %d not found or already processed", paymentID)
		}
		return fmt.Errorf("failed to get pending payment: %w", err)
	}

	// Parse deductions
	deductions := make(map[string]string)
	if len(deductionsJSON) > 0 {
		if err := json.Unmarshal(deductionsJSON, &deductions); err != nil {
			return fmt.Errorf("failed to parse deductions: %w", err)
		}
	}

	// Restore balance to each pool based on deductions
	for poolID, amountQuai := range deductions {
		// Restore the balance
		restoreQuery := `
			UPDATE balances
			SET amount = amount + $1::numeric, updated = NOW()
			WHERE poolid = $2 AND address = $3
		`
		if _, err := tx.Exec(restoreQuery, amountQuai, poolID, address); err != nil {
			return fmt.Errorf("failed to restore balance for pool %s: %w", poolID, err)
		}

		// Record the refund in balance changes
		changeQuery := `
			INSERT INTO balance_changes (poolid, address, amount, usage, created)
			VALUES ($1, $2, $3::numeric, 'payment_failed_refund', NOW())
		`
		if _, err := tx.Exec(changeQuery, poolID, address, amountQuai); err != nil {
			return fmt.Errorf("failed to record balance refund for pool %s: %w", poolID, err)
		}
	}

	// Mark as failed
	updateQuery := `UPDATE pending_payments SET status = 'failed', updated = NOW() WHERE id = $1`
	if _, err := tx.Exec(updateQuery, paymentID); err != nil {
		return fmt.Errorf("failed to mark payment as failed: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return fmt.Errorf("failed to commit transaction: %w", err)
	}

	log.Printf("Payment failed and refunded: address=%s amount=%s reason=%s", address, amountStr, reason)
	return nil
}

// GetPendingPayments returns all pending payments (for recovery on startup)
func (c *Client) GetPendingPayments() ([]*PendingPayment, error) {
	query := `
		SELECT id, address, amount::text, COALESCE(tx_hash, ''), status, deductions, created, updated
		FROM pending_payments
		WHERE service_id = $1 AND status = 'pending'
		ORDER BY created ASC
	`

	rows, err := c.db.Query(query, c.serviceID)
	if err != nil {
		return nil, fmt.Errorf("failed to query pending payments: %w", err)
	}
	defer rows.Close()

	var payments []*PendingPayment
	for rows.Next() {
		var p PendingPayment
		var amountStr string
		var deductionsJSON []byte
		if err := rows.Scan(&p.ID, &p.Address, &amountStr, &p.TxHash, &p.Status, &deductionsJSON, &p.Created, &p.Updated); err != nil {
			return nil, fmt.Errorf("failed to scan pending payment: %w", err)
		}
		// Convert from QUAI string to wei
		p.AmountWei = quaiToWeiPrecise(amountStr)

		// Parse deductions
		if len(deductionsJSON) > 0 {
			p.Deductions = make(map[string]string)
			if err := json.Unmarshal(deductionsJSON, &p.Deductions); err != nil {
				log.Printf("Warning: failed to parse deductions for payment %d: %v", p.ID, err)
			}
		}
		payments = append(payments, &p)
	}

	return payments, rows.Err()
}

// HasPendingPayment checks if there's already a pending payment for an address
func (c *Client) HasPendingPayment(address string) (bool, error) {
	query := `SELECT EXISTS(SELECT 1 FROM pending_payments WHERE service_id = $1 AND address = $2 AND status = 'pending')`
	var exists bool
	if err := c.db.QueryRow(query, c.serviceID, address).Scan(&exists); err != nil {
		return false, fmt.Errorf("failed to check pending payment: %w", err)
	}
	return exists, nil
}
