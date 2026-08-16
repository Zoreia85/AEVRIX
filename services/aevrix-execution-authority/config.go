package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/x509"
	"encoding/base64"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

type AuthorityConfig struct {
	DatabaseURL         string
	ClientID            string
	ClientSecret        []byte
	SigningKeyID        string
	SigningPrivateKey   *ecdsa.PrivateKey
	Port                string
	MaxClockSkew        time.Duration
	AttestationLifetime time.Duration
}

func LoadAuthorityConfig() (AuthorityConfig, error) {
	databaseURL := strings.TrimSpace(os.Getenv("DATABASE_URL"))
	if databaseURL == "" || !(strings.HasPrefix(databaseURL, "postgres://") || strings.HasPrefix(databaseURL, "postgresql://")) {
		return AuthorityConfig{}, errors.New("DATABASE_URL must be a PostgreSQL connection URL")
	}

	clientID := strings.TrimSpace(os.Getenv("AEVRIX_AUTHORITY_CLIENT_ID"))
	if !safeToken(clientID, 3, 120) {
		return AuthorityConfig{}, errors.New("AEVRIX_AUTHORITY_CLIENT_ID is missing or invalid")
	}
	secret, err := base64.StdEncoding.DecodeString(strings.TrimSpace(os.Getenv("AEVRIX_AUTHORITY_CLIENT_SECRET_B64")))
	if err != nil || len(secret) < 32 || len(secret) > 256 {
		return AuthorityConfig{}, errors.New("AEVRIX_AUTHORITY_CLIENT_SECRET_B64 must decode to 32..256 bytes")
	}

	keyID := strings.TrimSpace(os.Getenv("AEVRIX_AUTHORITY_SIGNING_KEY_ID"))
	if !safeToken(keyID, 3, 120) {
		return AuthorityConfig{}, errors.New("AEVRIX_AUTHORITY_SIGNING_KEY_ID is missing or invalid")
	}
	keyBytes, err := base64.StdEncoding.DecodeString(strings.TrimSpace(os.Getenv("AEVRIX_AUTHORITY_SIGNING_KEY_PKCS8_B64")))
	if err != nil || len(keyBytes) == 0 || len(keyBytes) > 16_384 {
		return AuthorityConfig{}, errors.New("AEVRIX_AUTHORITY_SIGNING_KEY_PKCS8_B64 is missing or invalid")
	}
	parsed, err := x509.ParsePKCS8PrivateKey(keyBytes)
	for i := range keyBytes {
		keyBytes[i] = 0
	}
	if err != nil {
		return AuthorityConfig{}, fmt.Errorf("parse authority signing key: %w", err)
	}
	privateKey, ok := parsed.(*ecdsa.PrivateKey)
	if !ok || privateKey.Curve != elliptic.P256() {
		return AuthorityConfig{}, errors.New("authority signing key must be ECDSA P-256 PKCS#8")
	}

	port := strings.TrimSpace(os.Getenv("PORT"))
	if port == "" {
		port = "8080"
	}
	if value, err := strconv.Atoi(port); err != nil || value < 1 || value > 65535 {
		return AuthorityConfig{}, errors.New("PORT is invalid")
	}

	maxSkew, err := durationSecondsFromEnv("AEVRIX_AUTHORITY_MAX_SKEW_SECONDS", 90, 30, 300)
	if err != nil {
		return AuthorityConfig{}, err
	}
	lifetime, err := durationSecondsFromEnv("AEVRIX_AUTHORITY_ATTESTATION_SECONDS", 300, 30, 3600)
	if err != nil {
		return AuthorityConfig{}, err
	}

	return AuthorityConfig{
		DatabaseURL:         databaseURL,
		ClientID:            clientID,
		ClientSecret:        secret,
		SigningKeyID:        keyID,
		SigningPrivateKey:   privateKey,
		Port:                port,
		MaxClockSkew:        maxSkew,
		AttestationLifetime: lifetime,
	}, nil
}

func durationSecondsFromEnv(name string, fallback, min, max int) (time.Duration, error) {
	raw := strings.TrimSpace(os.Getenv(name))
	if raw == "" {
		return time.Duration(fallback) * time.Second, nil
	}
	value, err := strconv.Atoi(raw)
	if err != nil || value < min || value > max {
		return 0, fmt.Errorf("%s must be an integer between %d and %d", name, min, max)
	}
	return time.Duration(value) * time.Second, nil
}
