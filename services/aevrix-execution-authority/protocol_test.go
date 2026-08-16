package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"testing"
	"time"
)

func TestPromotionEvidenceDigestMatchesDotNetVector(t *testing.T) {
	evidence := syntheticPromotionEvidence()
	digest, err := computePromotionEvidenceDigest(evidence)
	if err != nil {
		t.Fatalf("compute digest: %v", err)
	}
	const expected = "2064dc617a9710d1ff7f96c14628b58a28740e15cbcc3b16e680ee95d7acea8b"
	if digest != expected {
		t.Fatalf("cross-language digest mismatch: got %s want %s", digest, expected)
	}
	evidence.EvidenceDigestSHA256 = digest
	if err := validatePromotionEvidence(evidence); err != nil {
		t.Fatalf("validated vector rejected: %v", err)
	}
}

func TestPromotionSignerProducesVerifiableP256DERAttestation(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := NewPromotionSigner("authority-key-01", key, 5*time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	evidence := syntheticPromotionEvidence()
	digest, err := computePromotionEvidenceDigest(evidence)
	if err != nil {
		t.Fatal(err)
	}
	evidence.EvidenceDigestSHA256 = digest
	now := time.Date(2026, 8, 15, 18, 0, 0, 0, time.UTC)
	attestation, payload, err := signer.SignPromotion(evidence, now)
	if err != nil {
		t.Fatalf("sign promotion: %v", err)
	}

	signature, err := base64.StdEncoding.DecodeString(attestation.SignatureDERBase64)
	if err != nil {
		t.Fatalf("decode DER signature: %v", err)
	}
	digestBytes := sha256.Sum256(payload)
	if !ecdsa.VerifyASN1(&key.PublicKey, digestBytes[:], signature) {
		t.Fatal("ECDSA P-256 attestation signature did not verify")
	}

	spki, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	fp := sha256.Sum256(spki)
	if got, want := attestation.PublicKeySPKISHA256, hex.EncodeToString(fp[:]); got != want {
		t.Fatalf("SPKI fingerprint mismatch: got %s want %s", got, want)
	}
	if attestation.IssuedAtUnixSeconds != now.Unix() || attestation.ExpiresAtUnixSeconds != now.Add(5*time.Minute).Unix() {
		t.Fatal("attestation validity window mismatch")
	}
}

func TestPromotionEvidenceRejectsAuthorizationNotAtHead(t *testing.T) {
	evidence := syntheticPromotionEvidence()
	digest, err := computePromotionEvidenceDigest(evidence)
	if err != nil {
		t.Fatal(err)
	}
	evidence.EvidenceDigestSHA256 = digest
	evidence.AuthorizationRecordHashSHA256 = hashChar('4')
	if err := validatePromotionEvidence(evidence); err == nil {
		t.Fatal("expected authorization/head mismatch rejection")
	}
}

func TestPublicKeyEnvelopeContainsOnlyPublicMaterial(t *testing.T) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := NewPromotionSigner("authority-key-01", key, 5*time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	envelope := signer.PublicKeyEnvelope()
	spki, err := base64.StdEncoding.DecodeString(envelope.SubjectPublicKeyInfo)
	if err != nil {
		t.Fatal(err)
	}
	parsed, err := x509.ParsePKIXPublicKey(spki)
	if err != nil {
		t.Fatalf("public key envelope is not valid SPKI: %v", err)
	}
	if _, ok := parsed.(*ecdsa.PublicKey); !ok {
		t.Fatal("public key envelope did not decode as ECDSA public key")
	}
}

func syntheticPromotionEvidence() PromotionEvidenceRequest {
	return PromotionEvidenceRequest{
		Version:                       1,
		ProjectID:                     "11111111-1111-1111-1111-111111111111",
		RunID:                         "run-vector",
		ExecutionID:                   "exec-vector",
		CapabilityClass:               "coding-agent",
		CapabilityID:                  "sandbox-worker",
		ArtifactManifestSHA256:        hashChar('e'),
		ValidationDigestSHA256:        hashChar('f'),
		JudgeDecisionDigestSHA256:     hashChar('1'),
		PromotionDigestSHA256:         hashChar('2'),
		AuthorizationRecordHashSHA256: hashChar('3'),
		LedgerHead:                    Head{EntryCount: 5, HeadHashSHA256: hashChar('3')},
	}
}

func hashChar(value byte) string {
	result := make([]byte, 64)
	for i := range result {
		result[i] = value
	}
	return string(result)
}
