package config

import (
	"fmt"
	"os"
	"strings"

	"gopkg.in/yaml.v3"
)

// Config holds all configuration for the payout service
type Config struct {
	// Quai node RPC endpoint (for reading data: balances, receipts, etc.)
	QuaiRPC string `yaml:"quai_rpc"`

	// Quai RPC endpoint for sending transactions (optional, defaults to QuaiRPC)
	// Use this to send transactions through a public node while reading from local
	QuaiTxRPC string `yaml:"quai_tx_rpc"`

	// Chain ID for Quai network
	ChainID int64 `yaml:"chain_id"`

	// PostgreSQL connection string for miningcore database
	PostgresConnStr string `yaml:"postgres_conn_str"`

	// Coinbase addresses to track, keyed by miningcore pool ID
	// Example: {"quai-sha256": "0x123...", "quai-scrypt": "0x456..."}
	// The keys MUST match your miningcore pool IDs exactly
	CoinbaseAddresses map[string]string `yaml:"coinbase_addresses"`

	// Private keys for payout wallets, keyed by pool ID (must match CoinbaseAddresses keys)
	// Can be set via environment variables: POOL_PRIVATE_KEY_<POOLID> (uppercase, hyphens to underscores)
	// Example: POOL_PRIVATE_KEY_QUAI_SHA256 for pool "quai-sha256"
	// NOTE: If using keystore files, leave this empty and use keystore_paths instead
	PrivateKeys map[string]string `yaml:"private_keys"`

	// Paths to encrypted keystore files (Web3 Secret Storage format), keyed by pool ID
	// Example: {"quai-sha256": "/path/to/sha-key-encrypted.json", "quai-scrypt": "/path/to/scrypt-key.json"}
	// If provided, the service will prompt for a password at startup to decrypt the keystores
	// The keystore's embedded address will be used as the coinbase address (overrides coinbase_addresses)
	KeystorePaths map[string]string `yaml:"keystore_paths"`

	// Starting block height for initial scan (only used if no saved state exists)
	// Set this close to current block height to avoid scanning from genesis
	// If 0, will start from (current_height - 100) on first run
	StartBlockHeight int64 `yaml:"start_block_height"`

	// Confirmation depth before considering rewards confirmed (avoids reorgs)
	ConfirmationDepth int64 `yaml:"confirmation_depth"`

	// Coinbase maturity in blocks (how long until rewards unlock)
	// For testnet this is 100, for mainnet it's ~2 weeks worth of blocks
	CoinbaseMaturity int64 `yaml:"coinbase_maturity"`

	// Minimum payout threshold in QUAI (e.g., 0.1)
	MinPayoutThreshold float64 `yaml:"min_payout_threshold"`

	// How often to scan for new rewards (seconds)
	ScanIntervalSeconds int `yaml:"scan_interval_seconds"`

	// How often to process payouts (seconds)
	PayoutIntervalSeconds int `yaml:"payout_interval_seconds"`

	// Pool fee percentage (0.0 to 1.0)
	PoolFeePercent float64 `yaml:"pool_fee_percent"`

	// Address to send pool fees to
	PoolFeeAddress string `yaml:"pool_fee_address"`

	// Log level (debug, info, warn, error)
	LogLevel string `yaml:"log_level"`
}

// DefaultConfig returns a configuration with sensible defaults
func DefaultConfig() *Config {
	return &Config{
		QuaiRPC:               "http://localhost:8610",
		ChainID:               9000, // Quai testnet
		PostgresConnStr:       "host=localhost port=5432 user=miningcore dbname=miningcore sslmode=disable",
		ConfirmationDepth:     10,
		CoinbaseMaturity:      100, // Testnet default
		MinPayoutThreshold:    0.1,
		ScanIntervalSeconds:   30,
		PayoutIntervalSeconds: 300, // 5 minutes
		PoolFeePercent:        0.01,
		LogLevel:              "info",
		CoinbaseAddresses:     map[string]string{},
		PrivateKeys:           map[string]string{},
		KeystorePaths:         map[string]string{},
	}
}

// UsesKeystores returns true if the config uses keystore files instead of raw private keys
func (c *Config) UsesKeystores() bool {
	for _, path := range c.KeystorePaths {
		if path != "" {
			return true
		}
	}
	return false
}

// GetKeystorePoolIDs returns pool IDs that have keystore paths configured
func (c *Config) GetKeystorePoolIDs() []string {
	var poolIDs []string
	for poolID, path := range c.KeystorePaths {
		if path != "" {
			poolIDs = append(poolIDs, poolID)
		}
	}
	return poolIDs
}

// GetPoolIDs returns a list of all configured pool IDs (keys from CoinbaseAddresses with non-empty values)
func (c *Config) GetPoolIDs() []string {
	var poolIDs []string
	for poolID, addr := range c.CoinbaseAddresses {
		if addr != "" {
			poolIDs = append(poolIDs, poolID)
		}
	}
	return poolIDs
}

// LoadConfig loads configuration from a YAML file
func LoadConfig(path string) (*Config, error) {
	cfg := DefaultConfig()

	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	if err := yaml.Unmarshal(data, cfg); err != nil {
		return nil, err
	}

	return cfg, nil
}

// Validate checks that the configuration is valid
// Note: When using keystores, validation of keys happens after decryption in main.go
func (c *Config) Validate() error {
	// If using keystores, skip private key validation (will be handled after decryption)
	if c.UsesKeystores() {
		// Validate keystore files exist
		for poolID, path := range c.KeystorePaths {
			if path == "" {
				continue
			}
			if _, err := os.Stat(path); os.IsNotExist(err) {
				return fmt.Errorf("keystore file not found for pool %s: %s", poolID, path)
			}
		}
		// When using keystores, we don't require coinbase_addresses - they come from keystores
		if len(c.GetKeystorePoolIDs()) == 0 {
			return fmt.Errorf("at least one pool must be configured in keystore_paths")
		}
		return nil
	}

	// Traditional mode: validate private keys for each coinbase address
	for poolID, addr := range c.CoinbaseAddresses {
		if addr == "" {
			continue
		}

		privKey, hasKey := c.PrivateKeys[poolID]
		if !hasKey || privKey == "" {
			envVar := PoolIDToEnvVar(poolID)
			return fmt.Errorf("coinbase address configured for pool %s but no private key provided (set %s or private_keys.%s in config)",
				poolID, envVar, poolID)
		}
	}

	// Validate that at least one pool is configured
	if len(c.GetPoolIDs()) == 0 {
		return fmt.Errorf("at least one pool must be configured in coinbase_addresses")
	}

	return nil
}

// PoolIDToEnvVar converts a pool ID to its environment variable name for private key
// Example: "quai-sha256" -> "POOL_PRIVATE_KEY_QUAI_SHA256"
func PoolIDToEnvVar(poolID string) string {
	// Replace hyphens with underscores and uppercase
	normalized := strings.ToUpper(strings.ReplaceAll(poolID, "-", "_"))
	return "POOL_PRIVATE_KEY_" + normalized
}
