package rpc

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"strings"
	"time"
)

// Client is a JSON-RPC client for Quai node
type Client struct {
	endpoint   string
	httpClient *http.Client
}

// NewClient creates a new RPC client
func NewClient(endpoint string) *Client {
	return &Client{
		endpoint: endpoint,
		httpClient: &http.Client{
			Timeout: 30 * time.Second,
		},
	}
}

// RPCRequest represents a JSON-RPC request
type RPCRequest struct {
	JSONRPC string        `json:"jsonrpc"`
	Method  string        `json:"method"`
	Params  []interface{} `json:"params"`
	ID      int           `json:"id"`
}

// RPCResponse represents a JSON-RPC response
type RPCResponse struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      int             `json:"id"`
	Result  json.RawMessage `json:"result"`
	Error   *RPCError       `json:"error"`
}

// RPCError represents a JSON-RPC error
type RPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

func (e *RPCError) Error() string {
	return fmt.Sprintf("RPC error %d: %s", e.Code, e.Message)
}

// Call makes a JSON-RPC call
func (c *Client) Call(method string, params []interface{}, result interface{}) error {
	req := RPCRequest{
		JSONRPC: "2.0",
		Method:  method,
		Params:  params,
		ID:      1,
	}

	reqBody, err := json.Marshal(req)
	if err != nil {
		return fmt.Errorf("failed to marshal request: %w", err)
	}

	resp, err := c.httpClient.Post(c.endpoint, "application/json", bytes.NewReader(reqBody))
	if err != nil {
		return fmt.Errorf("failed to send request: %w", err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return fmt.Errorf("failed to read response: %w", err)
	}

	var rpcResp RPCResponse
	if err := json.Unmarshal(body, &rpcResp); err != nil {
		return fmt.Errorf("failed to unmarshal response: %w", err)
	}

	if rpcResp.Error != nil {
		return rpcResp.Error
	}

	if result != nil {
		if err := json.Unmarshal(rpcResp.Result, result); err != nil {
			return fmt.Errorf("failed to unmarshal result: %w", err)
		}
	}

	return nil
}

// BlockNumber returns the current block number
func (c *Client) BlockNumber() (int64, error) {
	var result string
	if err := c.Call("quai_blockNumber", []interface{}{}, &result); err != nil {
		return 0, err
	}

	result = strings.TrimPrefix(result, "0x")
	var height int64
	fmt.Sscanf(result, "%x", &height)
	return height, nil
}

// Transaction represents a transaction in a block
type Transaction struct {
	Hash    string `json:"hash"`
	Type    string `json:"type"`    // "0x2" = ExternalTxType
	EtxType string `json:"etxType"` // "0x1" = CoinbaseType
	To      string `json:"to"`
	Value   string `json:"value"`
	Input   string `json:"input"`
}

// WorkObjectHeader represents the header of a work object
type WorkObjectHeader struct {
	Number string `json:"number"`
}

// BlockData represents block data from RPC
type BlockData struct {
	Hash         string        `json:"hash"`
	WoHeader     WorkObjectHeader `json:"woHeader"`
	Transactions []Transaction `json:"transactions"`
}

// GetBlockByNumber fetches a block by number
func (c *Client) GetBlockByNumber(number int64) (*BlockData, error) {
	var result *BlockData
	hexNumber := fmt.Sprintf("0x%x", number)
	if err := c.Call("quai_getBlockByNumber", []interface{}{hexNumber, true}, &result); err != nil {
		return nil, err
	}
	return result, nil
}

// GetBalance returns the balance of an address
func (c *Client) GetBalance(address string) (*big.Int, error) {
	var result string
	if err := c.Call("quai_getBalance", []interface{}{address, "latest"}, &result); err != nil {
		return nil, err
	}

	balance := new(big.Int)
	result = strings.TrimPrefix(result, "0x")
	balance.SetString(result, 16)
	return balance, nil
}

// GetNonce returns the nonce for an address
func (c *Client) GetNonce(address string) (uint64, error) {
	var result string
	if err := c.Call("quai_getTransactionCount", []interface{}{address, "pending"}, &result); err != nil {
		return 0, err
	}

	result = strings.TrimPrefix(result, "0x")
	var nonce uint64
	fmt.Sscanf(result, "%x", &nonce)
	return nonce, nil
}

// GasPrice returns the suggested gas price
func (c *Client) GasPrice() (*big.Int, error) {
	var result string
	if err := c.Call("quai_gasPrice", []interface{}{}, &result); err != nil {
		return nil, err
	}

	gasPrice := new(big.Int)
	result = strings.TrimPrefix(result, "0x")
	gasPrice.SetString(result, 16)
	return gasPrice, nil
}

// EstimateGas estimates gas for a transaction
func (c *Client) EstimateGas(from, to string, value *big.Int) (uint64, error) {
	params := map[string]interface{}{
		"from":  from,
		"to":    to,
		"value": fmt.Sprintf("0x%x", value),
	}

	var result string
	if err := c.Call("quai_estimateGas", []interface{}{params}, &result); err != nil {
		return 0, err
	}

	result = strings.TrimPrefix(result, "0x")
	var gas uint64
	fmt.Sscanf(result, "%x", &gas)
	return gas, nil
}

// SendRawTransaction sends a signed transaction
func (c *Client) SendRawTransaction(txHex string) (string, error) {
	var result string
	if err := c.Call("quai_sendRawTransaction", []interface{}{txHex}, &result); err != nil {
		return "", err
	}
	return result, nil
}

// TransactionReceipt represents a transaction receipt
type TransactionReceipt struct {
	TransactionHash string `json:"transactionHash"`
	BlockNumber     string `json:"blockNumber"`
	Status          string `json:"status"`
	GasUsed         string `json:"gasUsed"`
}

// GetTransactionReceipt gets the receipt for a transaction
func (c *Client) GetTransactionReceipt(txHash string) (*TransactionReceipt, error) {
	var result *TransactionReceipt
	if err := c.Call("quai_getTransactionReceipt", []interface{}{txHash}, &result); err != nil {
		return nil, err
	}
	return result, nil
}
