package main

import (
	"bufio"
	"context"
	"flag"
	"fmt"
	"log"
	"math/big"
	"os"
	"os/signal"
	"strings"
	"syscall"

	"github.com/joho/godotenv"
	"golang.org/x/term"

	"github.com/miningcore/quai-payout-service/internal/config"
	"github.com/miningcore/quai-payout-service/internal/keystore"
	"github.com/miningcore/quai-payout-service/internal/payout"
	"github.com/miningcore/quai-payout-service/internal/postgres"
	"github.com/miningcore/quai-payout-service/internal/rpc"
	"github.com/miningcore/quai-payout-service/internal/tracker"
)

func main() {
	// Parse command line flags
	configPath := flag.String("config", "config.yaml", "Path to configuration file")
	flag.Parse()

	// Load environment variables from .env file
	if err := godotenv.Load(); err != nil {
		log.Printf("Warning: .env file not found")
	}

	// Load configuration
	cfg, err := config.LoadConfig(*configPath)
	if err != nil {
		log.Fatalf("Failed to load config: %v", err)
	}

	// Handle key loading based on configuration mode
	if cfg.UsesKeystores() {
		// Keystore mode: decrypt keystores with password
		if err := loadKeystores(cfg); err != nil {
			log.Fatalf("Failed to load keystores: %v", err)
		}
	} else {
		// Traditional mode: load private keys from environment variables
		// Environment variables override config file values
		// Format: POOL_PRIVATE_KEY_<POOL_ID_UPPERCASE_UNDERSCORED>
		// Example: POOL_PRIVATE_KEY_QUAI_SHA256 for pool ID "quai-sha256"
		for poolID := range cfg.CoinbaseAddresses {
			envVar := config.PoolIDToEnvVar(poolID)
			if key := os.Getenv(envVar); key != "" {
				if cfg.PrivateKeys == nil {
					cfg.PrivateKeys = make(map[string]string)
				}
				cfg.PrivateKeys[poolID] = key
				log.Printf("Loaded private key for %s from %s", poolID, envVar)
			}
		}
	}

	// Validate configuration (including private keys for each coinbase address)
	if err := cfg.Validate(); err != nil {
		log.Fatalf("Invalid configuration: %v", err)
	}

	log.Printf("Starting Quai Payout Service")
	poolIDs := cfg.GetPoolIDs()
	log.Printf("Pool IDs: %v", poolIDs)
	log.Printf("Quai RPC: %s", cfg.QuaiRPC)
	log.Printf("Confirmation depth: %d blocks", cfg.ConfirmationDepth)
	log.Printf("Coinbase maturity: %d blocks", cfg.CoinbaseMaturity)
	log.Printf("Min payout threshold: %.6f QUAI", cfg.MinPayoutThreshold)
	log.Printf("Pool fee: %.2f%%", cfg.PoolFeePercent*100)

	// Create context with cancellation
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	// Handle shutdown signals
	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-sigChan
		log.Printf("Shutdown signal received")
		cancel()
	}()

	// Create RPC client
	rpcClient := rpc.NewClient(cfg.QuaiRPC)

	// Test RPC connection
	blockNum, err := rpcClient.BlockNumber()
	if err != nil {
		log.Fatalf("Failed to connect to Quai node: %v", err)
	}
	log.Printf("Connected to Quai node, current block: %d", blockNum)

	// Create PostgreSQL client
	pgClient, err := postgres.NewClient(cfg.PostgresConnStr, poolIDs)
	if err != nil {
		log.Fatalf("Failed to connect to PostgreSQL: %v", err)
	}
	defer pgClient.Close()
	log.Printf("Connected to PostgreSQL")

	// Create coinbase tracker (uses PostgreSQL for state persistence)
	coinbaseTracker, err := tracker.NewCoinbaseTracker(cfg, rpcClient, pgClient)
	if err != nil {
		log.Fatalf("Failed to create coinbase tracker: %v", err)
	}

	// Set up tracker callbacks
	coinbaseTracker.SetOnRewardFound(func(reward *postgres.CoinbaseReward) {
		log.Printf("New reward detected: pool=%s block=%d value=%.6f QUAI",
			reward.PoolID, reward.BlockHeight, weiToQuai(reward.Value))
	})

	coinbaseTracker.SetOnRewardUnlocked(func(reward *postgres.CoinbaseReward) {
		log.Printf("Reward unlocked: pool=%s block=%d value=%.6f QUAI",
			reward.PoolID, reward.BlockHeight, weiToQuai(reward.Value))
	})

	// Create payout executor
	payoutExecutor, err := payout.NewExecutor(cfg, pgClient, coinbaseTracker)
	if err != nil {
		log.Fatalf("Failed to create payout executor: %v", err)
	}

	// Initialize executor with private keys from config
	if err := payoutExecutor.Initialize(); err != nil {
		log.Fatalf("Failed to initialize payout executor: %v", err)
	}
	for poolID, addr := range payoutExecutor.GetPoolAddresses() {
		log.Printf("Pool %s payout address: %s", poolID, addr)
	}

	// Check pool balance
	poolBalance, err := payoutExecutor.GetPoolBalance(ctx)
	if err != nil {
		log.Printf("Warning: Failed to get pool balance: %v", err)
	} else {
		log.Printf("Pool wallet balance: %.6f QUAI", weiToQuai(poolBalance))
	}

	// Start components
	go coinbaseTracker.Start(ctx)
	go payoutExecutor.Start(ctx)

	// Wait for shutdown
	<-ctx.Done()
	log.Printf("Shutting down...")

	// Give components time to cleanup
	log.Printf("Quai Payout Service stopped")
}

func weiToQuai(wei *big.Int) float64 {
	if wei == nil {
		return 0
	}
	f := new(big.Float).SetInt(wei)
	divisor := new(big.Float).SetFloat64(1e18)
	f.Quo(f, divisor)
	result, _ := f.Float64()
	return result
}

// loadKeystores prompts for a password and decrypts all configured keystores
func loadKeystores(cfg *config.Config) error {
	keystorePoolIDs := cfg.GetKeystorePoolIDs()
	if len(keystorePoolIDs) == 0 {
		return fmt.Errorf("no keystores configured")
	}

	// Display which keystores will be loaded
	fmt.Println("==============================================")
	fmt.Println("Encrypted Keystores Configured:")
	for _, poolID := range keystorePoolIDs {
		path := cfg.KeystorePaths[poolID]
		fmt.Printf("  - %s: %s\n", poolID, path)
	}
	fmt.Println("==============================================")

	// Prompt for password (same password for all keystores)
	password, err := readPassword("Enter keystore password: ")
	if err != nil {
		return fmt.Errorf("failed to read password: %w", err)
	}

	if password == "" {
		return fmt.Errorf("password cannot be empty")
	}

	fmt.Println("\nDecrypting keystores...")

	// Initialize maps if needed
	if cfg.PrivateKeys == nil {
		cfg.PrivateKeys = make(map[string]string)
	}
	if cfg.CoinbaseAddresses == nil {
		cfg.CoinbaseAddresses = make(map[string]string)
	}

	// Decrypt each keystore
	for _, poolID := range keystorePoolIDs {
		path := cfg.KeystorePaths[poolID]

		decrypted, err := keystore.LoadAndDecrypt(path, password)
		if err != nil {
			return fmt.Errorf("failed to decrypt keystore for %s: %w", poolID, err)
		}

		// Store the private key (as hex) and address in config
		cfg.PrivateKeys[poolID] = keystore.PrivateKeyToHex(decrypted.PrivateKey)
		cfg.CoinbaseAddresses[poolID] = decrypted.Address

		log.Printf("Decrypted keystore for %s: address=%s", poolID, decrypted.Address)
	}

	fmt.Printf("\nSuccessfully decrypted %d keystore(s)\n\n", len(keystorePoolIDs))
	return nil
}

// readPassword reads a password from the terminal without echoing
func readPassword(prompt string) (string, error) {
	fmt.Print(prompt)

	// Check if stdin is a terminal
	fd := int(syscall.Stdin)
	if term.IsTerminal(fd) {
		// Terminal mode: read without echo
		passwordBytes, err := term.ReadPassword(fd)
		fmt.Println() // Print newline after password entry
		if err != nil {
			return "", err
		}
		return strings.TrimSpace(string(passwordBytes)), nil
	}

	// Non-terminal mode (e.g., piped input): read normally
	reader := bufio.NewReader(os.Stdin)
	password, err := reader.ReadString('\n')
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(password), nil
}
