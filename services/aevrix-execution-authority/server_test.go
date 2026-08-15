package main

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestServerHealthAndPublicKeyAreMinimalAndUnauthenticated(t *testing.T) {
	store := newMemoryAuthorityStore()
	server := testAuthorityServer(t, store)

	health := httptest.NewRecorder()
	server.Handler().ServeHTTP(health, httptest.NewRequest(http.MethodGet, "http://authority.test/healthz", nil))
	if health.Code != http.StatusOK || health.Header().Get("Cache-Control") != "no-store" {
		t.Fatalf("health response invalid: status=%d cache=%q", health.Code, health.Header().Get("Cache-Control"))
	}

	publicKey := httptest.NewRecorder()
	server.Handler().ServeHTTP(publicKey, httptest.NewRequest(http.MethodGet, "http://authority.test/v1/public-key", nil))
	if publicKey.Code != http.StatusOK {
		t.Fatalf("public key response failed: %d", publicKey.Code)
	}
	var envelope PublicKeyEnvelope
	if err := json.Unmarshal(publicKey.Body.Bytes(), &envelope); err != nil {
		t.Fatal(err)
	}
	if envelope.KeyID != "authority-key-01" || envelope.Algorithm != "ECDSA_P256_SHA256_DER" || envelope.SubjectPublicKeyInfo == "" {
		t.Fatalf("public key envelope invalid: %+v", envelope)
	}
}

func TestServerHealthFailsClosedWhenStoreUnavailable(t *testing.T) {
	store := newMemoryAuthorityStore()
	store.pingErr = errors.New("offline")
	server := testAuthorityServer(t, store)
	recorder := httptest.NewRecorder()
	server.Handler().ServeHTTP(recorder, httptest.NewRequest(http.MethodGet, "http://authority.test/healthz", nil))
	if recorder.Code != http.StatusServiceUnavailable {
		t.Fatalf("unhealthy store did not fail health check: %d", recorder.Code)
	}
}

func TestServerAdvancesExactMonotonicHeadAndRejectsStalePredecessor(t *testing.T) {
	store := newMemoryAuthorityStore()
	server := testAuthorityServer(t, store)
	project := "11111111-1111-1111-1111-111111111111"
	first := Head{EntryCount: 1, HeadHashSHA256: hashChar('a')}
	request := AdvanceRequest{RequestID: "advance-001", ExpectedPrevious: EmptyHead, Next: first}
	body, _ := json.Marshal(request)
	req := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/projects/"+project+"/head/advance", body, "nonce-advance-012345678", testAuthorityNow)
	recorder := httptest.NewRecorder()
	server.Handler().ServeHTTP(recorder, req)
	if recorder.Code != http.StatusOK {
		t.Fatalf("first head advance failed: %d body=%s", recorder.Code, recorder.Body.String())
	}

	stale := AdvanceRequest{RequestID: "advance-002", ExpectedPrevious: EmptyHead, Next: Head{EntryCount: 1, HeadHashSHA256: hashChar('b')}}
	staleBody, _ := json.Marshal(stale)
	staleReq := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/projects/"+project+"/head/advance", staleBody, "nonce-advance-abcdefghijk", testAuthorityNow)
	staleRecorder := httptest.NewRecorder()
	server.Handler().ServeHTTP(staleRecorder, staleReq)
	if staleRecorder.Code != http.StatusConflict {
		t.Fatalf("stale predecessor was not rejected: %d", staleRecorder.Code)
	}
}

func TestServerAttestsOnlyCurrentAuthoritativeHead(t *testing.T) {
	store := newMemoryAuthorityStore()
	project := "11111111-1111-1111-1111-111111111111"
	store.heads[project] = Head{EntryCount: 5, HeadHashSHA256: hashChar('3')}
	server := testAuthorityServer(t, store)

	evidence := syntheticPromotionEvidence()
	digest, err := computePromotionEvidenceDigest(evidence)
	if err != nil {
		t.Fatal(err)
	}
	evidence.EvidenceDigestSHA256 = digest
	body, _ := json.Marshal(evidence)
	req := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/promotions/attest", body, "nonce-attest-0123456789", testAuthorityNow)
	recorder := httptest.NewRecorder()
	server.Handler().ServeHTTP(recorder, req)
	if recorder.Code != http.StatusOK {
		t.Fatalf("valid attestation request failed: %d body=%s", recorder.Code, recorder.Body.String())
	}
	if len(store.attestations) != 1 {
		t.Fatalf("attestation audit record not persisted: %d", len(store.attestations))
	}

	mismatch := evidence
	mismatch.LedgerHead = Head{EntryCount: 5, HeadHashSHA256: hashChar('4')}
	mismatch.AuthorizationRecordHashSHA256 = hashChar('4')
	mismatchDigest, err := computePromotionEvidenceDigest(mismatch)
	if err != nil {
		t.Fatal(err)
	}
	mismatch.EvidenceDigestSHA256 = mismatchDigest
	mismatchBody, _ := json.Marshal(mismatch)
	mismatchReq := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/promotions/attest", mismatchBody, "nonce-attest-abcdefghijk", testAuthorityNow)
	mismatchRecorder := httptest.NewRecorder()
	server.Handler().ServeHTTP(mismatchRecorder, mismatchReq)
	if mismatchRecorder.Code != http.StatusConflict {
		t.Fatalf("non-authoritative head was not rejected: %d body=%s", mismatchRecorder.Code, mismatchRecorder.Body.String())
	}
	if len(store.attestations) != 1 {
		t.Fatal("rejected head produced an attestation record")
	}
}

func TestServerProtectedEndpointRejectsUnknownJSONFields(t *testing.T) {
	store := newMemoryAuthorityStore()
	server := testAuthorityServer(t, store)
	project := "11111111-1111-1111-1111-111111111111"
	body := []byte(`{"requestId":"advance-001","expectedPrevious":{"entryCount":0,"headHashSha256":"` + genesisHash + `"},"next":{"entryCount":1,"headHashSha256":"` + hashChar('a') + `"},"unexpected":true}`)
	req := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/projects/"+project+"/head/advance", body, "nonce-json-0123456789abc", testAuthorityNow)
	recorder := httptest.NewRecorder()
	server.Handler().ServeHTTP(recorder, req)
	if recorder.Code != http.StatusBadRequest {
		t.Fatalf("unknown JSON field was not rejected: %d", recorder.Code)
	}
}

func testAuthorityServer(t *testing.T, store *memoryAuthorityStore) *AuthorityServer {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := NewPromotionSigner("authority-key-01", key, 5*time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	auth, err := NewAuthorityAuthenticator("client-test", testAuthoritySecret, 90*time.Second, store, func() time.Time { return testAuthorityNow })
	if err != nil {
		t.Fatal(err)
	}
	server, err := NewAuthorityServer(store, signer, auth, func() time.Time { return testAuthorityNow })
	if err != nil {
		t.Fatal(err)
	}
	return server
}

func bodyBytes(value any) []byte {
	buffer := bytes.NewBuffer(nil)
	_ = json.NewEncoder(buffer).Encode(value)
	return buffer.Bytes()
}
