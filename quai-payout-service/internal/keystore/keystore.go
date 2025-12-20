package keystore

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/ecdsa"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	"github.com/dominant-strategies/go-quai/common"
	"github.com/dominant-strategies/go-quai/crypto"
	"golang.org/x/crypto/scrypt"
)

// EncryptedKeystore represents a Web3 Secret Storage Definition keystore file
type EncryptedKeystore struct {
	Address string       `json:"address"`
	ID      string       `json:"id"`
	Version int          `json:"version"`
	Crypto  CryptoParams `json:"Crypto"`
}

// CryptoParams contains the encryption parameters
type CryptoParams struct {
	Cipher       string       `json:"cipher"`
	CipherText   string       `json:"ciphertext"`
	CipherParams CipherParams `json:"cipherparams"`
	KDF          string       `json:"kdf"`
	KDFParams    ScryptParams `json:"kdfparams"`
	MAC          string       `json:"mac"`
}

// CipherParams contains the initialization vector
type CipherParams struct {
	IV string `json:"iv"`
}

// ScryptParams contains scrypt key derivation parameters
type ScryptParams struct {
	DKLen int    `json:"dklen"`
	N     int    `json:"n"`
	P     int    `json:"p"`
	R     int    `json:"r"`
	Salt  string `json:"salt"`
}

// DecryptedKey holds the result of decrypting a keystore
type DecryptedKey struct {
	PrivateKey *ecdsa.PrivateKey
	Address    string // 0x prefixed address from keystore
}

// LoadAndDecrypt loads a keystore file and decrypts it with the given password
func LoadAndDecrypt(keystorePath, password string) (*DecryptedKey, error) {
	// Read the keystore file
	data, err := os.ReadFile(keystorePath)
	if err != nil {
		return nil, fmt.Errorf("failed to read keystore file: %w", err)
	}

	return DecryptKeystore(data, password)
}

// DecryptKeystore decrypts an encrypted keystore with the given password
func DecryptKeystore(keystoreJSON []byte, password string) (*DecryptedKey, error) {
	var ks EncryptedKeystore
	if err := json.Unmarshal(keystoreJSON, &ks); err != nil {
		return nil, fmt.Errorf("failed to parse keystore JSON: %w", err)
	}

	// Validate version
	if ks.Version != 3 {
		return nil, fmt.Errorf("unsupported keystore version %d (only version 3 supported)", ks.Version)
	}

	// Validate cipher
	if ks.Crypto.Cipher != "aes-128-ctr" {
		return nil, fmt.Errorf("unsupported cipher %s (only aes-128-ctr supported)", ks.Crypto.Cipher)
	}

	// Validate KDF
	if ks.Crypto.KDF != "scrypt" {
		return nil, fmt.Errorf("unsupported KDF %s (only scrypt supported)", ks.Crypto.KDF)
	}

	// Decode hex values
	salt, err := hex.DecodeString(ks.Crypto.KDFParams.Salt)
	if err != nil {
		return nil, fmt.Errorf("failed to decode salt: %w", err)
	}

	iv, err := hex.DecodeString(ks.Crypto.CipherParams.IV)
	if err != nil {
		return nil, fmt.Errorf("failed to decode IV: %w", err)
	}

	cipherText, err := hex.DecodeString(ks.Crypto.CipherText)
	if err != nil {
		return nil, fmt.Errorf("failed to decode ciphertext: %w", err)
	}

	mac, err := hex.DecodeString(ks.Crypto.MAC)
	if err != nil {
		return nil, fmt.Errorf("failed to decode MAC: %w", err)
	}

	// Derive key using scrypt
	derivedKey, err := scrypt.Key(
		[]byte(password),
		salt,
		ks.Crypto.KDFParams.N,
		ks.Crypto.KDFParams.R,
		ks.Crypto.KDFParams.P,
		ks.Crypto.KDFParams.DKLen,
	)
	if err != nil {
		return nil, fmt.Errorf("failed to derive key: %w", err)
	}

	// Verify MAC (Keccak-256 of last 16 bytes of derived key + ciphertext)
	// The MAC is calculated as: keccak256(derivedKey[16:32] + ciphertext)
	macData := append(derivedKey[16:32], cipherText...)
	calculatedMAC := crypto.Keccak256(macData)
	if !bytes.Equal(calculatedMAC, mac) {
		return nil, fmt.Errorf("MAC verification failed - incorrect password or corrupted keystore")
	}

	// Decrypt using AES-128-CTR
	// Use first 16 bytes of derived key as AES key
	block, err := aes.NewCipher(derivedKey[:16])
	if err != nil {
		return nil, fmt.Errorf("failed to create AES cipher: %w", err)
	}

	stream := cipher.NewCTR(block, iv)
	privateKeyBytes := make([]byte, len(cipherText))
	stream.XORKeyStream(privateKeyBytes, cipherText)

	// Parse the private key
	privateKey, err := crypto.ToECDSA(privateKeyBytes)
	if err != nil {
		return nil, fmt.Errorf("failed to parse private key: %w", err)
	}

	// Format address with 0x prefix
	address := "0x" + strings.ToLower(ks.Address)
	if !strings.HasPrefix(ks.Address, "0x") && !strings.HasPrefix(ks.Address, "0X") {
		address = "0x" + strings.ToLower(ks.Address)
	} else {
		address = strings.ToLower(ks.Address)
	}

	// Verify the derived address matches the keystore address
	derivedAddress := crypto.PubkeyToAddress(privateKey.PublicKey, common.Location{0, 0})
	expectedAddr := strings.ToLower(strings.TrimPrefix(address, "0x"))
	actualAddr := strings.ToLower(strings.TrimPrefix(derivedAddress.Hex(), "0x"))

	if expectedAddr != actualAddr {
		return nil, fmt.Errorf("address mismatch: keystore says %s but derived %s", address, derivedAddress.Hex())
	}

	return &DecryptedKey{
		PrivateKey: privateKey,
		Address:    derivedAddress.Hex(),
	}, nil
}

// PrivateKeyToHex converts a private key to a hex string (without 0x prefix)
func PrivateKeyToHex(key *ecdsa.PrivateKey) string {
	return hex.EncodeToString(crypto.FromECDSA(key))
}
