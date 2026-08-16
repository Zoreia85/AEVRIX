package main

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

type AuthorityServer struct {
	store   AuthorityStore
	signer  *PromotionSigner
	auth    *AuthorityAuthenticator
	now     func() time.Time
	handler http.Handler
}

func NewAuthorityServer(
	store AuthorityStore,
	signer *PromotionSigner,
	auth *AuthorityAuthenticator,
	now func() time.Time,
) (*AuthorityServer, error) {
	if store == nil {
		return nil, errors.New("execution authority store is required")
	}
	if signer == nil {
		return nil, errors.New("execution authority signer is required")
	}
	if auth == nil {
		return nil, errors.New("execution authority authenticator is required")
	}
	if now == nil {
		now = time.Now
	}

	s := &AuthorityServer{store: store, signer: signer, auth: auth, now: now}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.handleHealth)
	mux.HandleFunc("GET /v1/public-key", s.handlePublicKey)
	mux.Handle("GET /v1/projects/{projectID}/head", auth.Middleware(http.HandlerFunc(s.handleLoadHead)))
	mux.Handle("POST /v1/projects/{projectID}/head/advance", auth.Middleware(http.HandlerFunc(s.handleAdvanceHead)))
	mux.Handle("POST /v1/promotions/attest", auth.Middleware(http.HandlerFunc(s.handleAttestPromotion)))
	s.handler = securityHeaders(mux)
	return s, nil
}

func (s *AuthorityServer) Handler() http.Handler {
	return s.handler
}

func (s *AuthorityServer) handleHealth(w http.ResponseWriter, r *http.Request) {
	if err := s.store.Ping(r.Context()); err != nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]string{"status": "unavailable"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "ok"})
}

func (s *AuthorityServer) handlePublicKey(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, s.signer.PublicKeyEnvelope())
}

func (s *AuthorityServer) handleLoadHead(w http.ResponseWriter, r *http.Request) {
	projectID := strings.ToLower(strings.TrimSpace(r.PathValue("projectID")))
	if !validUUID(projectID) {
		writeAuthorityError(w, http.StatusBadRequest, "invalid_project")
		return
	}
	head, err := s.store.LoadHead(r.Context(), projectID)
	if errors.Is(err, ErrHeadNotFound) {
		writeAuthorityError(w, http.StatusNotFound, "head_not_found")
		return
	}
	if err != nil {
		writeAuthorityError(w, http.StatusServiceUnavailable, "authority_store_unavailable")
		return
	}
	writeJSON(w, http.StatusOK, head)
}

func (s *AuthorityServer) handleAdvanceHead(w http.ResponseWriter, r *http.Request) {
	projectID := strings.ToLower(strings.TrimSpace(r.PathValue("projectID")))
	if !validUUID(projectID) {
		writeAuthorityError(w, http.StatusBadRequest, "invalid_project")
		return
	}
	client, ok := AuthenticatedClientFromContext(r.Context())
	if !ok {
		writeAuthorityError(w, http.StatusUnauthorized, "authentication_required")
		return
	}
	var request AdvanceRequest
	if err := decodeStrictJSON(r, &request); err != nil {
		writeAuthorityError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if !safeToken(request.RequestID, 3, 160) ||
		validateHead(request.ExpectedPrevious, true) != nil ||
		validateHead(request.Next, false) != nil ||
		request.Next.EntryCount != request.ExpectedPrevious.EntryCount+1 {
		writeAuthorityError(w, http.StatusBadRequest, "invalid_head_transition")
		return
	}

	confirmed, err := s.store.AdvanceHead(
		r.Context(),
		projectID,
		request.RequestID,
		request.ExpectedPrevious,
		request.Next,
		client.ClientID,
	)
	if errors.Is(err, ErrConflict) {
		writeAuthorityError(w, http.StatusConflict, "head_conflict")
		return
	}
	if err != nil {
		writeAuthorityError(w, http.StatusServiceUnavailable, "authority_store_unavailable")
		return
	}
	writeJSON(w, http.StatusOK, confirmed)
}

func (s *AuthorityServer) handleAttestPromotion(w http.ResponseWriter, r *http.Request) {
	client, ok := AuthenticatedClientFromContext(r.Context())
	if !ok {
		writeAuthorityError(w, http.StatusUnauthorized, "authentication_required")
		return
	}
	var evidence PromotionEvidenceRequest
	if err := decodeStrictJSON(r, &evidence); err != nil {
		writeAuthorityError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	evidence.ProjectID = strings.ToLower(strings.TrimSpace(evidence.ProjectID))
	if err := validatePromotionEvidence(evidence); err != nil {
		writeAuthorityError(w, http.StatusBadRequest, "invalid_promotion_evidence")
		return
	}

	authoritativeHead, err := s.store.LoadHead(r.Context(), evidence.ProjectID)
	if errors.Is(err, ErrHeadNotFound) {
		writeAuthorityError(w, http.StatusConflict, "head_not_authoritative")
		return
	}
	if err != nil {
		writeAuthorityError(w, http.StatusServiceUnavailable, "authority_store_unavailable")
		return
	}
	if authoritativeHead.EntryCount != evidence.LedgerHead.EntryCount ||
		!constantHexEqual(authoritativeHead.HeadHashSHA256, evidence.LedgerHead.HeadHashSHA256) {
		writeAuthorityError(w, http.StatusConflict, "head_not_authoritative")
		return
	}

	attestation, _, err := s.signer.SignPromotion(evidence, s.now().UTC())
	if err != nil {
		writeAuthorityError(w, http.StatusInternalServerError, "attestation_failed")
		return
	}
	signature, err := base64.StdEncoding.DecodeString(attestation.SignatureDERBase64)
	if err != nil {
		writeAuthorityError(w, http.StatusInternalServerError, "attestation_failed")
		return
	}
	record := AttestationRecord{
		Nonce:                attestation.Nonce,
		ProjectID:            attestation.ProjectID,
		RunID:                attestation.RunID,
		ExecutionID:          attestation.ExecutionID,
		EvidenceDigestSHA256: attestation.EvidenceDigestSHA256,
		Head: Head{
			EntryCount:     attestation.HeadEntryCount,
			HeadHashSHA256: attestation.HeadHashSHA256,
		},
		KeyID:        attestation.KeyID,
		IssuedAt:     time.Unix(attestation.IssuedAtUnixSeconds, 0).UTC(),
		ExpiresAt:    time.Unix(attestation.ExpiresAtUnixSeconds, 0).UTC(),
		SignatureDER: signature,
		ClientID:     client.ClientID,
	}
	if err := s.store.RecordAttestation(r.Context(), record); err != nil {
		if errors.Is(err, ErrConflict) {
			writeAuthorityError(w, http.StatusConflict, "attestation_already_exists")
			return
		}
		writeAuthorityError(w, http.StatusServiceUnavailable, "authority_store_unavailable")
		return
	}
	writeJSON(w, http.StatusOK, attestation)
}

func decodeStrictJSON(r *http.Request, destination any) error {
	if r.ContentLength > maximumRequestBytes {
		return fmt.Errorf("request body exceeds maximum size")
	}
	if mediaType := r.Header.Get("Content-Type"); mediaType != "" && !strings.HasPrefix(strings.ToLower(mediaType), "application/json") {
		return fmt.Errorf("content type must be application/json")
	}
	decoder := json.NewDecoder(io.LimitReader(r.Body, maximumRequestBytes+1))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(destination); err != nil {
		return err
	}
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		return fmt.Errorf("request contains trailing JSON content")
	}
	return nil
}

func securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Pragma", "no-cache")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "no-referrer")
		next.ServeHTTP(w, r)
	})
}

func writeAuthorityError(w http.ResponseWriter, status int, code string) {
	writeJSON(w, status, map[string]string{"error": code})
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
