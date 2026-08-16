package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"
)

var testAuthoritySecret = bytes.Repeat([]byte{0x5a}, 32)
var testAuthorityNow = time.Date(2026, 8, 15, 18, 0, 0, 0, time.UTC)

func TestAuthenticationAcceptsBoundHMACAndConsumesNonce(t *testing.T) {
	store := newMemoryAuthorityStore()
	auth, err := NewAuthorityAuthenticator("client-test", testAuthoritySecret, 90*time.Second, store, func() time.Time { return testAuthorityNow })
	if err != nil {
		t.Fatal(err)
	}
	called := false
	handler := auth.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		called = true
		client, ok := AuthenticatedClientFromContext(r.Context())
		if !ok || client.ClientID != "client-test" {
			t.Fatal("authenticated client context missing")
		}
		body := make([]byte, r.ContentLength)
		_, _ = r.Body.Read(body)
		if string(body) != `{"value":1}` {
			t.Fatalf("restored request body mismatch: %q", body)
		}
		w.WriteHeader(http.StatusNoContent)
	}))

	req := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/promotions/attest", []byte(`{"value":1}`), "nonce-0123456789abcdef", testAuthorityNow)
	recorder := httptest.NewRecorder()
	handler.ServeHTTP(recorder, req)
	if recorder.Code != http.StatusNoContent || !called {
		t.Fatalf("valid HMAC request rejected: status=%d called=%v", recorder.Code, called)
	}
}

func TestAuthenticationRejectsReplayNonce(t *testing.T) {
	store := newMemoryAuthorityStore()
	auth, err := NewAuthorityAuthenticator("client-test", testAuthoritySecret, 90*time.Second, store, func() time.Time { return testAuthorityNow })
	if err != nil {
		t.Fatal(err)
	}
	handler := auth.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))

	for attempt := 0; attempt < 2; attempt++ {
		req := signedAuthorityRequest(http.MethodGet, "http://authority.test/v1/projects/11111111-1111-1111-1111-111111111111/head", nil, "nonce-replay-0123456789", testAuthorityNow)
		recorder := httptest.NewRecorder()
		handler.ServeHTTP(recorder, req)
		if attempt == 0 && recorder.Code != http.StatusNoContent {
			t.Fatalf("first nonce use failed: %d", recorder.Code)
		}
		if attempt == 1 && recorder.Code != http.StatusConflict {
			t.Fatalf("replayed nonce not rejected with conflict: %d", recorder.Code)
		}
	}
}

func TestAuthenticationRejectsAlteredBodyWithoutConsumingNonce(t *testing.T) {
	store := newMemoryAuthorityStore()
	auth, err := NewAuthorityAuthenticator("client-test", testAuthoritySecret, 90*time.Second, store, func() time.Time { return testAuthorityNow })
	if err != nil {
		t.Fatal(err)
	}
	handler := auth.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))
	nonce := "nonce-body-0123456789ab"

	forged := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/promotions/attest", []byte(`{"value":1}`), nonce, testAuthorityNow)
	forged.Body = ioNopCloserString(`{"value":2}`)
	forged.ContentLength = int64(len(`{"value":2}`))
	forgedRecorder := httptest.NewRecorder()
	handler.ServeHTTP(forgedRecorder, forged)
	if forgedRecorder.Code != http.StatusUnauthorized {
		t.Fatalf("altered body was not rejected: %d", forgedRecorder.Code)
	}

	valid := signedAuthorityRequest(http.MethodPost, "http://authority.test/v1/promotions/attest", []byte(`{"value":1}`), nonce, testAuthorityNow)
	validRecorder := httptest.NewRecorder()
	handler.ServeHTTP(validRecorder, valid)
	if validRecorder.Code != http.StatusNoContent {
		t.Fatalf("nonce was consumed before signature verification: %d", validRecorder.Code)
	}
}

func TestAuthenticationRejectsStaleTimestampAndQueryString(t *testing.T) {
	store := newMemoryAuthorityStore()
	auth, err := NewAuthorityAuthenticator("client-test", testAuthoritySecret, 90*time.Second, store, func() time.Time { return testAuthorityNow })
	if err != nil {
		t.Fatal(err)
	}
	handler := auth.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusNoContent) }))

	stale := signedAuthorityRequest(http.MethodGet, "http://authority.test/v1/projects/11111111-1111-1111-1111-111111111111/head", nil, "nonce-stale-0123456789a", testAuthorityNow.Add(-5*time.Minute))
	staleRecorder := httptest.NewRecorder()
	handler.ServeHTTP(staleRecorder, stale)
	if staleRecorder.Code != http.StatusUnauthorized {
		t.Fatalf("stale request was not rejected: %d", staleRecorder.Code)
	}

	queried := signedAuthorityRequest(http.MethodGet, "http://authority.test/v1/projects/11111111-1111-1111-1111-111111111111/head?x=1", nil, "nonce-query-0123456789a", testAuthorityNow)
	queryRecorder := httptest.NewRecorder()
	handler.ServeHTTP(queryRecorder, queried)
	if queryRecorder.Code != http.StatusBadRequest {
		t.Fatalf("query-bearing authority request was not rejected: %d", queryRecorder.Code)
	}
}

func signedAuthorityRequest(method, rawURL string, body []byte, nonce string, timestamp time.Time) *http.Request {
	request := httptest.NewRequest(method, rawURL, bytes.NewReader(body))
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}
	bodyHash := sha256.Sum256(body)
	bodyHashHex := hex.EncodeToString(bodyHash[:])
	timestampRaw := strconv.FormatInt(timestamp.UTC().Unix(), 10)
	canonical := strings.Join([]string{
		requestProtocolLabel,
		strings.ToUpper(method),
		request.URL.EscapedPath(),
		timestampRaw,
		nonce,
		bodyHashHex,
	}, "\n")
	mac := hmac.New(sha256.New, testAuthoritySecret)
	_, _ = mac.Write([]byte(canonical))
	request.Header.Set("X-AEVRIX-Client-Id", "client-test")
	request.Header.Set("X-AEVRIX-Timestamp", timestampRaw)
	request.Header.Set("X-AEVRIX-Nonce", nonce)
	request.Header.Set("X-AEVRIX-Body-SHA256", bodyHashHex)
	request.Header.Set("X-AEVRIX-Request-Signature", base64.StdEncoding.EncodeToString(mac.Sum(nil)))
	return request
}

func ioNopCloserString(value string) io.ReadCloser {
	return io.NopCloser(strings.NewReader(value))
}
