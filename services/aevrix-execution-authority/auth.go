package main

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

const (
	requestProtocolLabel = "AEVRIX-AUTHORITY-REQUEST-V1"
	maximumRequestBytes  = 64 * 1024
)

type authContextKey struct{}

type AuthenticatedClient struct {
	ClientID string
	Nonce    string
	Observed time.Time
}

type AuthorityAuthenticator struct {
	clientID string
	secret   []byte
	maxSkew  time.Duration
	store    AuthorityStore
	now      func() time.Time
}

func NewAuthorityAuthenticator(
	clientID string,
	secret []byte,
	maxSkew time.Duration,
	store AuthorityStore,
	now func() time.Time,
) (*AuthorityAuthenticator, error) {
	if !safeToken(clientID, 3, 120) {
		return nil, fmt.Errorf("invalid authority client id")
	}
	if len(secret) < 32 || len(secret) > 256 {
		return nil, fmt.Errorf("authority HMAC secret must contain 32..256 bytes")
	}
	if maxSkew < 30*time.Second || maxSkew > 5*time.Minute {
		return nil, fmt.Errorf("authority maximum clock skew must be between 30 seconds and 5 minutes")
	}
	if store == nil {
		return nil, fmt.Errorf("authority store is required")
	}
	if now == nil {
		now = time.Now
	}
	copySecret := append([]byte(nil), secret...)
	return &AuthorityAuthenticator{
		clientID: clientID,
		secret:   copySecret,
		maxSkew:  maxSkew,
		store:    store,
		now:      now,
	}, nil
}

func (a *AuthorityAuthenticator) Middleware(next http.Handler) http.Handler {
	if next == nil {
		panic("authority authentication middleware requires a handler")
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		client, body, status, err := a.authenticate(r)
		if err != nil {
			writeAuthorityError(w, status, "authentication_failed")
			return
		}
		r.Body = io.NopCloser(bytes.NewReader(body))
		r.ContentLength = int64(len(body))
		ctx := context.WithValue(r.Context(), authContextKey{}, client)
		next.ServeHTTP(w, r.WithContext(ctx))
	})
}

func AuthenticatedClientFromContext(ctx context.Context) (AuthenticatedClient, bool) {
	client, ok := ctx.Value(authContextKey{}).(AuthenticatedClient)
	return client, ok
}

func (a *AuthorityAuthenticator) authenticate(r *http.Request) (AuthenticatedClient, []byte, int, error) {
	if r == nil || r.URL == nil {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("missing request")
	}
	if r.URL.RawQuery != "" || r.URL.Fragment != "" {
		return AuthenticatedClient{}, nil, http.StatusBadRequest, fmt.Errorf("query and fragment are forbidden on authority API")
	}
	if r.Host == "" {
		return AuthenticatedClient{}, nil, http.StatusBadRequest, fmt.Errorf("host is required")
	}

	clientID := strings.TrimSpace(r.Header.Get("X-AEVRIX-Client-Id"))
	if clientID != a.clientID {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("unknown client")
	}
	timestampRaw := strings.TrimSpace(r.Header.Get("X-AEVRIX-Timestamp"))
	timestamp, err := strconv.ParseInt(timestampRaw, 10, 64)
	if err != nil || timestamp <= 0 {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("invalid timestamp")
	}
	observed := a.now().UTC()
	requestTime := time.Unix(timestamp, 0).UTC()
	delta := observed.Sub(requestTime)
	if delta < 0 {
		delta = -delta
	}
	if delta > a.maxSkew {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("request timestamp outside allowed skew")
	}

	nonce := strings.TrimSpace(r.Header.Get("X-AEVRIX-Nonce"))
	if !safeToken(nonce, 16, 128) {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("invalid nonce")
	}

	body, err := readBoundedRequestBody(r.Body, maximumRequestBytes)
	if err != nil {
		return AuthenticatedClient{}, nil, http.StatusRequestEntityTooLarge, err
	}
	computedBodyHash := sha256.Sum256(body)
	bodyHashHex := hex.EncodeToString(computedBodyHash[:])
	declaredBodyHash := strings.ToLower(strings.TrimSpace(r.Header.Get("X-AEVRIX-Body-SHA256")))
	if !constantHexEqual(bodyHashHex, declaredBodyHash) {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("body digest mismatch")
	}

	canonical := strings.Join([]string{
		requestProtocolLabel,
		strings.ToUpper(r.Method),
		r.URL.EscapedPath(),
		timestampRaw,
		nonce,
		bodyHashHex,
	}, "\n")
	expected := hmac.New(sha256.New, a.secret)
	_, _ = expected.Write([]byte(canonical))
	expectedMAC := expected.Sum(nil)

	declaredMAC, err := base64.StdEncoding.DecodeString(strings.TrimSpace(r.Header.Get("X-AEVRIX-Request-Signature")))
	if err != nil || len(declaredMAC) != sha256.Size || !hmac.Equal(expectedMAC, declaredMAC) {
		return AuthenticatedClient{}, nil, http.StatusUnauthorized, fmt.Errorf("request signature mismatch")
	}

	if err := a.store.UseNonce(r.Context(), clientID, nonce, observed); err != nil {
		if err == ErrReplay {
			return AuthenticatedClient{}, nil, http.StatusConflict, err
		}
		return AuthenticatedClient{}, nil, http.StatusServiceUnavailable, err
	}

	return AuthenticatedClient{ClientID: clientID, Nonce: nonce, Observed: observed}, body, http.StatusOK, nil
}

func readBoundedRequestBody(body io.ReadCloser, maximum int64) ([]byte, error) {
	if body == nil {
		return []byte{}, nil
	}
	defer body.Close()
	reader := io.LimitReader(body, maximum+1)
	data, err := io.ReadAll(reader)
	if err != nil {
		return nil, fmt.Errorf("read authority request: %w", err)
	}
	if int64(len(data)) > maximum {
		return nil, fmt.Errorf("authority request exceeds %d bytes", maximum)
	}
	return data, nil
}
