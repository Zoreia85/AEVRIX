package main

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"
)

const (
	promotionAttestationProtocol = "AEVRIX-PROMOTION-ATTESTATION-V1"
	promotionEvidenceVersion     = 1
	genesisHash                  = "0000000000000000000000000000000000000000000000000000000000000000"
)

type Head struct {
	EntryCount     int64  `json:"entryCount"`
	HeadHashSHA256 string `json:"headHashSha256"`
}

var EmptyHead = Head{EntryCount: 0, HeadHashSHA256: genesisHash}

type AdvanceRequest struct {
	RequestID        string `json:"requestId"`
	ExpectedPrevious Head   `json:"expectedPrevious"`
	Next             Head   `json:"next"`
}

type PromotionEvidenceRequest struct {
	Version                       int    `json:"version"`
	ProjectID                     string `json:"projectId"`
	RunID                         string `json:"runId"`
	ExecutionID                   string `json:"executionId"`
	CapabilityClass               string `json:"capabilityClass"`
	CapabilityID                  string `json:"capabilityId"`
	ArtifactManifestSHA256        string `json:"artifactManifestSha256"`
	ValidationDigestSHA256        string `json:"validationDigestSha256"`
	JudgeDecisionDigestSHA256     string `json:"judgeDecisionDigestSha256"`
	PromotionDigestSHA256         string `json:"promotionDigestSha256"`
	AuthorizationRecordHashSHA256 string `json:"authorizationRecordHashSha256"`
	LedgerHead                    Head   `json:"ledgerHead"`
	EvidenceDigestSHA256          string `json:"evidenceDigestSha256"`
}

type PromotionAuthorityAttestation struct {
	Version              int    `json:"version"`
	KeyID                string `json:"keyId"`
	ProjectID            string `json:"projectId"`
	RunID                string `json:"runId"`
	ExecutionID          string `json:"executionId"`
	EvidenceDigestSHA256 string `json:"evidenceDigestSha256"`
	HeadEntryCount       int64  `json:"headEntryCount"`
	HeadHashSHA256       string `json:"headHashSha256"`
	IssuedAtUnixSeconds  int64  `json:"issuedAtUnixSeconds"`
	ExpiresAtUnixSeconds int64  `json:"expiresAtUnixSeconds"`
	Nonce                string `json:"nonce"`
	SignatureDERBase64   string `json:"signatureDerBase64"`
	PublicKeySPKISHA256  string `json:"publicKeySpkiSha256"`
}

type PublicKeyEnvelope struct {
	KeyID                string `json:"keyId"`
	Algorithm            string `json:"algorithm"`
	SubjectPublicKeyInfo string `json:"subjectPublicKeyInfoBase64"`
	SPKISHA256           string `json:"spkiSha256"`
}

type PromotionSigner struct {
	keyID       string
	privateKey  *ecdsa.PrivateKey
	spki        []byte
	fingerprint string
	lifetime    time.Duration
}

func NewPromotionSigner(keyID string, privateKey *ecdsa.PrivateKey, lifetime time.Duration) (*PromotionSigner, error) {
	if !safeToken(keyID, 3, 120) {
		return nil, errors.New("invalid signing key id")
	}
	if privateKey == nil || privateKey.Curve == nil || privateKey.Curve.Params().Name != "P-256" {
		return nil, errors.New("signing key must be ECDSA P-256")
	}
	if lifetime < 30*time.Second || lifetime > time.Hour {
		return nil, errors.New("attestation lifetime must be between 30 seconds and 1 hour")
	}
	spki, err := x509.MarshalPKIXPublicKey(&privateKey.PublicKey)
	if err != nil {
		return nil, fmt.Errorf("marshal signing public key: %w", err)
	}
	fp := sha256.Sum256(spki)
	return &PromotionSigner{
		keyID:       keyID,
		privateKey:  privateKey,
		spki:        spki,
		fingerprint: hex.EncodeToString(fp[:]),
		lifetime:    lifetime,
	}, nil
}

func (s *PromotionSigner) PublicKeyEnvelope() PublicKeyEnvelope {
	return PublicKeyEnvelope{
		KeyID:                s.keyID,
		Algorithm:            "ECDSA_P256_SHA256_DER",
		SubjectPublicKeyInfo: base64.StdEncoding.EncodeToString(s.spki),
		SPKISHA256:           s.fingerprint,
	}
}

func (s *PromotionSigner) SignPromotion(e PromotionEvidenceRequest, now time.Time) (PromotionAuthorityAttestation, []byte, error) {
	if err := validatePromotionEvidence(e); err != nil {
		return PromotionAuthorityAttestation{}, nil, err
	}
	nonceBytes := make([]byte, 16)
	if _, err := rand.Read(nonceBytes); err != nil {
		return PromotionAuthorityAttestation{}, nil, fmt.Errorf("generate attestation nonce: %w", err)
	}
	issued := now.UTC().Unix()
	attestation := PromotionAuthorityAttestation{
		Version:              1,
		KeyID:                s.keyID,
		ProjectID:            strings.ToLower(e.ProjectID),
		RunID:                e.RunID,
		ExecutionID:          e.ExecutionID,
		EvidenceDigestSHA256: strings.ToLower(e.EvidenceDigestSHA256),
		HeadEntryCount:       e.LedgerHead.EntryCount,
		HeadHashSHA256:       strings.ToLower(e.LedgerHead.HeadHashSHA256),
		IssuedAtUnixSeconds:  issued,
		ExpiresAtUnixSeconds: now.UTC().Add(s.lifetime).Unix(),
		Nonce:                hex.EncodeToString(nonceBytes),
		PublicKeySPKISHA256:  s.fingerprint,
	}
	payload, err := canonicalAttestationPayload(attestation)
	if err != nil {
		return PromotionAuthorityAttestation{}, nil, err
	}
	digest := sha256.Sum256(payload)
	signature, err := ecdsa.SignASN1(rand.Reader, s.privateKey, digest[:])
	if err != nil {
		return PromotionAuthorityAttestation{}, nil, fmt.Errorf("sign promotion attestation: %w", err)
	}
	attestation.SignatureDERBase64 = base64.StdEncoding.EncodeToString(signature)
	return attestation, payload, nil
}

func canonicalAttestationPayload(a PromotionAuthorityAttestation) ([]byte, error) {
	if a.Version != 1 || !safeToken(a.KeyID, 3, 120) || !validUUID(a.ProjectID) ||
		!safeToken(a.RunID, 3, 160) || !safeToken(a.ExecutionID, 3, 160) ||
		!validSHA256(a.EvidenceDigestSHA256) || a.HeadEntryCount <= 0 ||
		!validSHA256(a.HeadHashSHA256) || a.IssuedAtUnixSeconds <= 0 ||
		a.ExpiresAtUnixSeconds <= a.IssuedAtUnixSeconds || !safeToken(a.Nonce, 16, 128) {
		return nil, errors.New("invalid promotion attestation structure")
	}
	canonical := strings.Join([]string{
		promotionAttestationProtocol,
		strconv.Itoa(a.Version),
		a.KeyID,
		strings.ToLower(a.ProjectID),
		a.RunID,
		a.ExecutionID,
		strings.ToLower(a.EvidenceDigestSHA256),
		strconv.FormatInt(a.HeadEntryCount, 10),
		strings.ToLower(a.HeadHashSHA256),
		strconv.FormatInt(a.IssuedAtUnixSeconds, 10),
		strconv.FormatInt(a.ExpiresAtUnixSeconds, 10),
		a.Nonce,
	}, "\n")
	return []byte(canonical), nil
}

func computePromotionEvidenceDigest(e PromotionEvidenceRequest) (string, error) {
	if e.Version != promotionEvidenceVersion || !validUUID(e.ProjectID) ||
		!safeToken(e.RunID, 3, 160) || !safeToken(e.ExecutionID, 3, 160) ||
		!safeToken(e.CapabilityClass, 2, 80) || !safeToken(e.CapabilityID, 2, 160) ||
		!validSHA256(e.ArtifactManifestSHA256) || !validSHA256(e.ValidationDigestSHA256) ||
		!validSHA256(e.JudgeDecisionDigestSHA256) || !validSHA256(e.PromotionDigestSHA256) ||
		!validSHA256(e.AuthorizationRecordHashSHA256) || e.LedgerHead.EntryCount <= 0 ||
		!validSHA256(e.LedgerHead.HeadHashSHA256) {
		return "", errors.New("invalid promotion evidence structure")
	}
	canonical := strings.Join([]string{
		strconv.Itoa(e.Version),
		strings.ToLower(e.ProjectID),
		e.RunID,
		e.ExecutionID,
		e.CapabilityClass,
		e.CapabilityID,
		strings.ToLower(e.ArtifactManifestSHA256),
		strings.ToLower(e.ValidationDigestSHA256),
		strings.ToLower(e.JudgeDecisionDigestSHA256),
		strings.ToLower(e.PromotionDigestSHA256),
		strings.ToLower(e.AuthorizationRecordHashSHA256),
		strconv.FormatInt(e.LedgerHead.EntryCount, 10),
		strings.ToLower(e.LedgerHead.HeadHashSHA256),
	}, "\n")
	digest := sha256.Sum256([]byte(canonical))
	return hex.EncodeToString(digest[:]), nil
}

func validatePromotionEvidence(e PromotionEvidenceRequest) error {
	computed, err := computePromotionEvidenceDigest(e)
	if err != nil {
		return err
	}
	if !constantHexEqual(computed, e.EvidenceDigestSHA256) {
		return errors.New("promotion evidence digest does not match canonical content")
	}
	if !constantHexEqual(e.AuthorizationRecordHashSHA256, e.LedgerHead.HeadHashSHA256) {
		return errors.New("promotion authorization record is not the anchored head")
	}
	return nil
}

func validateHead(h Head, allowEmpty bool) error {
	if allowEmpty && h.EntryCount == 0 && strings.EqualFold(h.HeadHashSHA256, genesisHash) {
		return nil
	}
	if h.EntryCount <= 0 || !validSHA256(h.HeadHashSHA256) || strings.EqualFold(h.HeadHashSHA256, genesisHash) {
		return errors.New("invalid execution authority head")
	}
	return nil
}

func validSHA256(value string) bool {
	if len(value) != 64 {
		return false
	}
	_, err := hex.DecodeString(value)
	return err == nil
}

func constantHexEqual(left, right string) bool {
	if !validSHA256(left) || !validSHA256(right) {
		return false
	}
	a, _ := hex.DecodeString(left)
	b, _ := hex.DecodeString(right)
	if len(a) != len(b) {
		return false
	}
	var diff byte
	for i := range a {
		diff |= a[i] ^ b[i]
	}
	return diff == 0
}

func safeToken(value string, min, max int) bool {
	if len(value) < min || len(value) > max {
		return false
	}
	for _, r := range value {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' || r == '.' || r == ':' {
			continue
		}
		return false
	}
	return true
}

func validUUID(value string) bool {
	if len(value) != 36 || value[8] != '-' || value[13] != '-' || value[18] != '-' || value[23] != '-' {
		return false
	}
	for i, r := range value {
		if i == 8 || i == 13 || i == 18 || i == 23 {
			continue
		}
		if !((r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') || (r >= 'A' && r <= 'F')) {
			return false
		}
	}
	return true
}
