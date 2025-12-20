package payout

import (
	"context"
	"crypto/ecdsa"
	"fmt"
	"log"
	"math/big"
	"sync"
	"time"

	quai "github.com/dominant-strategies/go-quai"
	"github.com/dominant-strategies/go-quai/common"
	"github.com/dominant-strategies/go-quai/core/types"
	"github.com/dominant-strategies/go-quai/crypto"
	"github.com/dominant-strategies/go-quai/quaiclient/ethclient"

	"github.com/miningcore/quai-payout-service/internal/config"
	"github.com/miningcore/quai-payout-service/internal/postgres"
	"github.com/miningcore/quai-payout-service/internal/tracker"
)

// PayoutResult represents the result of a payout attempt
type PayoutResult struct {
	Address    string
	Amount     *big.Int
	AmountQuai float64
	TxHash     string
	Success    bool
	Error      error
	GasUsed    uint64
	GasPrice   *big.Int
}

// Wallet holds the credentials and address for a payout wallet
type Wallet struct {
	PoolID     string
	PrivateKey *ecdsa.PrivateKey
	Address    common.Address
	Signer     types.Signer
}

// Executor handles payout operations
type Executor struct {
	cfg      *config.Config
	pgClient *postgres.Client
	tracker  *tracker.CoinbaseTracker

	// Quai SDK client for reading data (balances, receipts, etc.)
	quaiClient *ethclient.Client

	// Quai SDK client for sending transactions (may be different RPC)
	quaiTxClient *ethclient.Client

	// Wallets per pool ID (e.g., "quai-sha256", "quai-scrypt")
	wallets map[string]*Wallet

	mu sync.Mutex
}

// NewExecutor creates a new payout executor
func NewExecutor(cfg *config.Config, pgClient *postgres.Client, tracker *tracker.CoinbaseTracker) (*Executor, error) {
	return &Executor{
		cfg:      cfg,
		pgClient: pgClient,
		tracker:  tracker,
		wallets:  make(map[string]*Wallet),
	}, nil
}

// Initialize initializes the executor with private keys for each pool
func (e *Executor) Initialize() error {
	location := common.Location{0, 0} // Zone 0-0

	// Connect to Quai node for reading data
	client, err := ethclient.Dial(e.cfg.QuaiRPC)
	if err != nil {
		return fmt.Errorf("failed to connect to Quai node: %w", err)
	}
	e.quaiClient = client

	// Connect to Quai node for sending transactions (may be different RPC)
	txRPC := e.cfg.QuaiTxRPC
	if txRPC == "" {
		txRPC = e.cfg.QuaiRPC // Default to main RPC if not specified
	}
	txClient, err := ethclient.Dial(txRPC)
	if err != nil {
		return fmt.Errorf("failed to connect to Quai tx node: %w", err)
	}
	e.quaiTxClient = txClient
	log.Printf("Transaction RPC: %s", txRPC)

	// Initialize wallet for each configured pool
	for poolID, privateKeyHex := range e.cfg.PrivateKeys {
		if privateKeyHex == "" {
			continue
		}

		// Check if this pool has a coinbase address configured
		coinbaseAddr, hasCoinbase := e.cfg.CoinbaseAddresses[poolID]
		if !hasCoinbase || coinbaseAddr == "" {
			log.Printf("Warning: private key provided for %s but no coinbase address configured", poolID)
			continue
		}

		// Remove 0x prefix if present
		if len(privateKeyHex) > 2 && privateKeyHex[:2] == "0x" {
			privateKeyHex = privateKeyHex[2:]
		}

		// Parse private key
		privateKey, err := crypto.HexToECDSA(privateKeyHex)
		if err != nil {
			return fmt.Errorf("invalid private key for %s: %w", poolID, err)
		}

		// Derive address from private key
		derivedAddress := crypto.PubkeyToAddress(privateKey.PublicKey, location)

		// Verify derived address matches configured coinbase address
		expectedAddr := common.HexToAddress(coinbaseAddr, location)
		if derivedAddress.Hex() != expectedAddr.Hex() {
			return fmt.Errorf("private key for %s derives address %s but coinbase address is %s",
				poolID, derivedAddress.Hex(), coinbaseAddr)
		}

		// Create signer for this wallet
		signer := types.NewSigner(big.NewInt(e.cfg.ChainID), location)

		e.wallets[poolID] = &Wallet{
			PoolID:     poolID,
			PrivateKey: privateKey,
			Address:    derivedAddress,
			Signer:     signer,
		}

		log.Printf("Initialized %s wallet: %s", poolID, derivedAddress.Hex())
	}

	if len(e.wallets) == 0 {
		return fmt.Errorf("no wallets initialized - check private_keys configuration")
	}

	// Ensure pending payments table exists
	if err := e.pgClient.EnsurePendingPaymentsTable(); err != nil {
		return fmt.Errorf("failed to create pending payments table: %w", err)
	}

	// Register the PPLNS calculator with the tracker
	// This will be called when rewards are discovered to snapshot scores
	e.tracker.SetPPLNSCalculator(e.calculatePPLNSScores)

	return nil
}

// calculatePPLNSScores calculates current PPLNS scores for a specific pool
// This is called by the tracker when a reward is discovered
func (e *Executor) calculatePPLNSScores(poolID string) (map[string]float64, error) {
	pplnsWindow := 24 * time.Hour
	return e.pgClient.CalculatePPLNSScores(pplnsWindow, poolID)
}

// Start begins the payout processing loop
func (e *Executor) Start(ctx context.Context) {
	log.Printf("Starting payout executor, interval: %d seconds", e.cfg.PayoutIntervalSeconds)

	// Check for any pending payments from previous runs
	e.recoverPendingPayments(ctx)

	ticker := time.NewTicker(time.Duration(e.cfg.PayoutIntervalSeconds) * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			log.Printf("Payout executor stopped")
			return
		case <-ticker.C:
			e.processPayouts(ctx)
		}
	}
}

// recoverPendingPayments checks for pending payments from previous runs and attempts to resolve them
func (e *Executor) recoverPendingPayments(ctx context.Context) {
	pendingPayments, err := e.pgClient.GetPendingPayments()
	if err != nil {
		log.Printf("Error getting pending payments: %v", err)
		return
	}

	if len(pendingPayments) == 0 {
		return
	}

	log.Printf("Found %d pending payments from previous run, checking status...", len(pendingPayments))

	for _, payment := range pendingPayments {
		if payment.TxHash == "" {
			// No tx was broadcast - fail and refund
			log.Printf("Pending payment %d has no tx hash, refunding...", payment.ID)
			if err := e.pgClient.FailPendingPayment(payment.ID, "no transaction broadcast"); err != nil {
				log.Printf("Error failing payment %d: %v", payment.ID, err)
			}
			continue
		}

		// Check if the transaction was confirmed
		txHash := common.HexToHash(payment.TxHash)
		receipt, err := e.quaiClient.TransactionReceipt(ctx, txHash)
		if err != nil || receipt == nil {
			// Transaction not found - it may have been dropped
			// Check how old it is
			age := time.Since(payment.Created)
			if age > 30*time.Minute {
				// Old pending payment with no receipt - likely dropped, refund
				log.Printf("Pending payment %d (tx=%s) not found after %v, refunding...",
					payment.ID, payment.TxHash, age)
				if err := e.pgClient.FailPendingPayment(payment.ID, "transaction not found after timeout"); err != nil {
					log.Printf("Error failing payment %d: %v", payment.ID, err)
				}
			} else {
				log.Printf("Pending payment %d (tx=%s) still waiting for confirmation (%v old)",
					payment.ID, payment.TxHash, age)
			}
			continue
		}

		// Check receipt status
		if receipt.Status == 1 {
			// Transaction succeeded
			log.Printf("Pending payment %d (tx=%s) confirmed successfully", payment.ID, payment.TxHash)
			if err := e.pgClient.ConfirmPendingPayment(payment.ID, payment.TxHash); err != nil {
				log.Printf("Error confirming payment %d: %v", payment.ID, err)
			}
		} else {
			// Transaction failed on-chain
			log.Printf("Pending payment %d (tx=%s) failed on-chain, refunding...", payment.ID, payment.TxHash)
			if err := e.pgClient.FailPendingPayment(payment.ID, "transaction failed on-chain"); err != nil {
				log.Printf("Error failing payment %d: %v", payment.ID, err)
			}
		}
	}
}

// processPayouts processes pending payouts
func (e *Executor) processPayouts(ctx context.Context) {
	e.mu.Lock()
	defer e.mu.Unlock()

	// First, distribute any unlocked rewards to miner balances
	if err := e.distributeUnlockedRewards(ctx); err != nil {
		log.Printf("Error distributing rewards: %v", err)
	}

	// Then, process payouts for miners above threshold
	if err := e.sendPayouts(ctx); err != nil {
		log.Printf("Error sending payouts: %v", err)
	}
}

// distributeUnlockedRewards distributes unlocked coinbase rewards to miner balances
// Uses atomic distribution to prevent double-payout on crash or concurrent instances
func (e *Executor) distributeUnlockedRewards(ctx context.Context) error {
	unlockedRewards := e.tracker.GetUnlockedRewards()
	if len(unlockedRewards) == 0 {
		return nil
	}

	log.Printf("Processing %d unlocked rewards", len(unlockedRewards))

	for _, reward := range unlockedRewards {
		if reward.IsPaidOut {
			continue
		}

		// Use the PPLNS scores that were snapshot at discovery time
		scores := reward.MinerScores
		if len(scores) == 0 {
			log.Printf("Warning: No miner scores for reward block=%d tx=%s, skipping",
				reward.BlockHeight, reward.TxHash)
			// Mark as paid out atomically to avoid retrying forever
			_, _ = e.pgClient.DistributeRewardAtomically(reward.TxHash, reward.PoolID, nil)
			continue
		}

		// Calculate pool fee
		poolFee := new(big.Float).SetInt(reward.Value)
		poolFee.Mul(poolFee, new(big.Float).SetFloat64(e.cfg.PoolFeePercent))
		poolFeeInt, _ := poolFee.Int(nil)

		// Distributable amount after pool fee
		distributable := new(big.Int).Sub(reward.Value, poolFeeInt)

		log.Printf("Distributing reward: block=%d value=%.6f QUAI (pool fee: %.6f) to %d miners",
			reward.BlockHeight,
			weiToQuai(reward.Value),
			weiToQuai(poolFeeInt),
			len(scores))

		// Build distribution list for atomic operation
		var distributions []postgres.RewardDistribution

		// Add miner distributions based on PPLNS scores snapshot from discovery time
		for minerAddr, score := range scores {
			minerShare := new(big.Float).SetInt(distributable)
			minerShare.Mul(minerShare, new(big.Float).SetFloat64(score))
			minerShareInt, _ := minerShare.Int(nil)

			if minerShareInt.Cmp(big.NewInt(0)) <= 0 {
				continue
			}

			distributions = append(distributions, postgres.RewardDistribution{
				Address:   minerAddr,
				AmountWei: minerShareInt,
				Usage:     "block_reward",
			})

			log.Printf("  -> %s: %.6f QUAI (%.2f%%)", minerAddr, weiToQuai(minerShareInt), score*100)
		}

		// Add pool fee to distribution list
		if poolFeeInt.Cmp(big.NewInt(0)) > 0 && e.cfg.PoolFeeAddress != "" {
			distributions = append(distributions, postgres.RewardDistribution{
				Address:   e.cfg.PoolFeeAddress,
				AmountWei: poolFeeInt,
				Usage:     "pool_fee",
			})
		}

		// Atomically mark reward as paid out AND distribute all balances
		// This prevents double-distribution even with multiple service instances or failures
		distributed, err := e.pgClient.DistributeRewardAtomically(reward.TxHash, reward.PoolID, distributions)
		if err != nil {
			log.Printf("Error distributing reward %s: %v", reward.TxHash, err)
			continue
		}

		if distributed {
			log.Printf("Reward %s distributed successfully to %d recipients", reward.TxHash, len(distributions))
		} else {
			log.Printf("Reward %s was already distributed (idempotent skip)", reward.TxHash)
		}
	}

	return nil
}

// sendPayouts sends payouts to miners above threshold using atomic pending payment pattern
func (e *Executor) sendPayouts(ctx context.Context) error {
	// Get all miners with balance above minimum threshold
	balances, err := e.pgClient.GetMinerBalances(e.cfg.MinPayoutThreshold)
	if err != nil {
		return fmt.Errorf("failed to get miner balances: %w", err)
	}

	if len(balances) == 0 {
		return nil
	}

	log.Printf("Processing payouts for %d miners", len(balances))

	// Get balances for all wallets
	walletBalances := make(map[string]*big.Int)
	totalBalance := big.NewInt(0)

	for poolID, wallet := range e.wallets {
		balance, err := e.quaiClient.BalanceAt(ctx, wallet.Address.MixedcaseAddress(), nil)
		if err != nil {
			log.Printf("Error getting balance for %s wallet: %v", poolID, err)
			continue
		}
		walletBalances[poolID] = balance
		totalBalance.Add(totalBalance, balance)
		log.Printf("%s wallet balance: %.6f QUAI", poolID, weiToQuai(balance))
	}

	log.Printf("Total pool balance: %.6f QUAI", weiToQuai(totalBalance))

	for _, balance := range balances {
		select {
		case <-ctx.Done():
			return ctx.Err()
		default:
		}

		// Note: We don't skip miners with pending payments because:
		// 1. Balance is deducted atomically when pending payment is created
		// 2. New rewards accumulate as NEW balance after deduction
		// 3. A new payout only pays the new balance, not the pending amount
		// This allows miners to receive continuous payouts even if one is stuck

		// Check if miner has custom threshold
		threshold, err := e.pgClient.GetMinerPayoutThreshold(balance.Address)
		if err != nil {
			log.Printf("Error getting threshold for %s: %v", balance.Address, err)
			continue
		}
		if threshold <= 0 {
			threshold = e.cfg.MinPayoutThreshold
		}

		// Skip if below threshold (compare wei to threshold converted to wei)
		thresholdWei := quaiToWei(threshold)
		if balance.AmountWei.Cmp(thresholdWei) < 0 {
			continue
		}

		// Use wei amount directly from balance
		amountWei := balance.AmountWei

		// Select wallet based on the pool where the balance was earned
		selectedPoolID := balance.PoolId
		selectedWallet, ok := e.wallets[selectedPoolID]
		if !ok {
			log.Printf("No wallet configured for pool %s (miner %s has %.6f QUAI)",
				selectedPoolID, balance.Address, weiToQuai(amountWei))
			continue
		}

		// Check wallet has sufficient balance
		if walletBalances[selectedPoolID] == nil || walletBalances[selectedPoolID].Cmp(amountWei) < 0 {
			log.Printf("Insufficient %s wallet balance for payout to %s (need %.6f, have %.6f)",
				selectedPoolID, balance.Address, weiToQuai(amountWei),
				weiToQuai(walletBalances[selectedPoolID]))
			continue
		}

		// STEP 1: Create pending payment and deduct balance atomically BEFORE broadcast
		paymentID, err := e.pgClient.CreatePendingPayment(selectedPoolID, balance.Address, amountWei)
		if err != nil {
			log.Printf("Error creating pending payment for %s: %v", balance.Address, err)
			continue
		}

		log.Printf("Created pending payment %d for %s: %.6f QUAI", paymentID, balance.Address, weiToQuai(amountWei))

		// STEP 2: Build and sign the transaction (but don't send yet)
		signedTx, buildErr := e.buildSignedTransaction(ctx, selectedWallet, balance.Address, amountWei)
		if buildErr != nil {
			log.Printf("Failed to build transaction for %s: %v, refunding...", balance.Address, buildErr)
			if err := e.pgClient.FailPendingPayment(paymentID, buildErr.Error()); err != nil {
				log.Printf("Error failing payment %d: %v", paymentID, err)
			}
			continue
		}

		// STEP 3: Store tx hash BEFORE broadcasting
		// This ensures we can check on-chain status on recovery even if broadcast succeeds but DB update fails
		txHash := signedTx.Hash().Hex()
		if err := e.pgClient.SetPendingPaymentTxHash(paymentID, txHash); err != nil {
			// CRITICAL: If we can't store the tx hash, DO NOT broadcast
			// Otherwise we could broadcast, crash, restart, and refund even though tx may confirm
			log.Printf("Failed to store tx hash for payment %d: %v, refunding...", paymentID, err)
			if err := e.pgClient.FailPendingPayment(paymentID, "failed to store tx hash: "+err.Error()); err != nil {
				log.Printf("Error failing payment %d: %v", paymentID, err)
			}
			continue
		}

		// STEP 4: Now broadcast the transaction (tx hash is safely stored)
		// Use the dedicated tx RPC endpoint (may be different from read endpoint)
		if err := e.quaiTxClient.SendTransaction(ctx, signedTx); err != nil {
			// Broadcast failed - but tx hash is stored, so on recovery we'll check on-chain status
			// before refunding (the tx might have actually been received by some nodes)
			log.Printf("Broadcast failed for %s: %v (tx hash %s stored, will verify on-chain before refund)",
				balance.Address, err, txHash)
			// Don't immediately refund - leave as pending with tx hash
			// Recovery logic will check if this tx exists on-chain before refunding
			continue
		}

		log.Printf("Transaction broadcast for %s: tx=%s, waiting for receipt...", balance.Address, txHash)

		// STEP 5: Wait for receipt and check status
		confirmed, receiptStatus := e.waitForReceipt(ctx, txHash)

		if confirmed && receiptStatus == 1 {
			// Transaction confirmed successfully
			if err := e.pgClient.ConfirmPendingPayment(paymentID, txHash); err != nil {
				log.Printf("Error confirming payment %d: %v", paymentID, err)
			} else {
				log.Printf("Payout confirmed from %s wallet: %s -> %.6f QUAI, tx=%s",
					selectedPoolID, balance.Address, weiToQuai(amountWei), txHash)
			}

			// Update wallet and total balance tracking
			walletBalances[selectedPoolID].Sub(walletBalances[selectedPoolID], amountWei)
			totalBalance.Sub(totalBalance, amountWei)
		} else if confirmed && receiptStatus != 1 {
			// Transaction failed on-chain - refund
			log.Printf("Transaction failed on-chain for %s (tx=%s), refunding...", balance.Address, txHash)
			if err := e.pgClient.FailPendingPayment(paymentID, "transaction failed on-chain"); err != nil {
				log.Printf("Error failing payment %d: %v", paymentID, err)
			}
		} else {
			// No receipt yet - leave as pending, will be recovered on next run
			log.Printf("Transaction %s for %s still pending confirmation (will check on next cycle)",
				txHash, balance.Address)
		}
	}

	return nil
}

// buildSignedTransaction builds and signs a payout transaction without broadcasting it.
// Returns the signed transaction so caller can extract the hash before deciding to broadcast.
func (e *Executor) buildSignedTransaction(ctx context.Context, wallet *Wallet, toAddress string, amount *big.Int) (*types.Transaction, error) {
	// Get nonce
	nonce, err := e.quaiClient.PendingNonceAt(ctx, wallet.Address.MixedcaseAddress())
	if err != nil {
		return nil, fmt.Errorf("failed to get nonce: %w", err)
	}

	// Get gas price
	gasPrice, err := e.quaiClient.SuggestGasPrice(ctx)
	if err != nil {
		return nil, fmt.Errorf("failed to get gas price: %w", err)
	}
	log.Printf("Gas price: %s", gasPrice)

	// Parse recipient address
	to := common.HexToAddress(toAddress, common.Location{0, 0})

	// Estimate gas
	estimatedGas, err := e.quaiClient.EstimateGas(ctx, quai.CallMsg{
		From:     wallet.Address,
		To:       &to,
		Gas:      0,
		GasPrice: gasPrice,
		Value:    amount,
		Data:     []byte{},
	})
	if err != nil {
		return nil, fmt.Errorf("failed to estimate gas: %w", err)
	}
	log.Printf("Estimated gas: %d", estimatedGas)

	// Build transaction
	inner := &types.QuaiTx{
		ChainID:    big.NewInt(e.cfg.ChainID),
		Nonce:      nonce,
		GasPrice:   gasPrice,
		Gas:        estimatedGas,
		To:         &to,
		Value:      amount,
		Data:       []byte{},
		AccessList: types.AccessList{},
		V:          big.NewInt(0),
		R:          big.NewInt(0),
		S:          big.NewInt(0),
	}
	tx := types.NewTx(inner)

	// Sign transaction with this wallet's key and signer
	signedTx, err := types.SignTx(tx, wallet.Signer, wallet.PrivateKey)
	if err != nil {
		return nil, fmt.Errorf("failed to sign transaction: %w", err)
	}

	return signedTx, nil
}

// waitForReceipt waits for a transaction receipt with retries
// Returns (confirmed, receiptStatus) where:
//   - confirmed=true, status=1: transaction succeeded
//   - confirmed=true, status=0: transaction failed on-chain
//   - confirmed=false: no receipt yet (transaction may still be pending or dropped)
func (e *Executor) waitForReceipt(ctx context.Context, txHashHex string) (confirmed bool, receiptStatus uint64) {
	txHash := common.HexToHash(txHashHex)

	// Wait for receipt with retries (up to ~2.5 minutes)
	for i := 0; i < 15; i++ {
		select {
		case <-ctx.Done():
			return false, 0
		case <-time.After(10 * time.Second):
		}

		receipt, err := e.quaiClient.TransactionReceipt(ctx, txHash)
		if err == nil && receipt != nil {
			return true, receipt.Status
		}
	}

	// No receipt after retries - transaction may still be pending or was dropped
	log.Printf("ERROR: No receipt for tx %s after 2.5 minutes - transaction may be stuck or dropped", txHashHex)
	return false, 0
}

// GetPoolBalance returns the total balance across all pool wallets
func (e *Executor) GetPoolBalance(ctx context.Context) (*big.Int, error) {
	total := big.NewInt(0)
	for _, wallet := range e.wallets {
		balance, err := e.quaiClient.BalanceAt(ctx, wallet.Address.MixedcaseAddress(), nil)
		if err != nil {
			return nil, fmt.Errorf("failed to get balance for %s wallet: %w", wallet.PoolID, err)
		}
		total.Add(total, balance)
	}
	return total, nil
}

// GetPoolAddresses returns all pool payout addresses by pool ID
func (e *Executor) GetPoolAddresses() map[string]string {
	addresses := make(map[string]string)
	for poolID, wallet := range e.wallets {
		addresses[poolID] = wallet.Address.Hex()
	}
	return addresses
}

// Helper functions

func weiToQuai(wei *big.Int) float64 {
	f := new(big.Float).SetInt(wei)
	divisor := new(big.Float).SetFloat64(1e18)
	f.Quo(f, divisor)
	result, _ := f.Float64()
	return result
}

func quaiToWei(quai float64) *big.Int {
	f := new(big.Float).SetFloat64(quai)
	multiplier := new(big.Float).SetFloat64(1e18)
	f.Mul(f, multiplier)
	result, _ := f.Int(nil)
	return result
}
