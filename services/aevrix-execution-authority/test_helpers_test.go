package main

import (
	"context"
	"errors"
	"sync"
	"time"
)

type memoryAuthorityStore struct {
	mu           sync.Mutex
	heads        map[string]Head
	nonces       map[string]struct{}
	requests     map[string]advanceMemoryRecord
	attestations []AttestationRecord
	pingErr      error
}

type advanceMemoryRecord struct {
	ProjectID string
	ClientID  string
	Expected  Head
	Next      Head
}

func newMemoryAuthorityStore() *memoryAuthorityStore {
	return &memoryAuthorityStore{
		heads:    map[string]Head{},
		nonces:   map[string]struct{}{},
		requests: map[string]advanceMemoryRecord{},
	}
}

func (s *memoryAuthorityStore) Ping(context.Context) error {
	return s.pingErr
}

func (s *memoryAuthorityStore) UseNonce(_ context.Context, clientID, nonce string, _ time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	key := clientID + "\x00" + nonce
	if _, exists := s.nonces[key]; exists {
		return ErrReplay
	}
	s.nonces[key] = struct{}{}
	return nil
}

func (s *memoryAuthorityStore) LoadHead(_ context.Context, projectID string) (Head, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	head, ok := s.heads[projectID]
	if !ok {
		return Head{}, ErrHeadNotFound
	}
	return head, nil
}

func (s *memoryAuthorityStore) AdvanceHead(
	_ context.Context,
	projectID string,
	requestID string,
	expected Head,
	next Head,
	clientID string,
) (Head, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if prior, ok := s.requests[requestID]; ok {
		if prior.ProjectID == projectID && prior.ClientID == clientID && prior.Expected == expected && prior.Next == next {
			return next, nil
		}
		return Head{}, ErrConflict
	}
	current, ok := s.heads[projectID]
	if !ok {
		current = EmptyHead
	}
	if current != expected || next.EntryCount != expected.EntryCount+1 {
		return Head{}, ErrConflict
	}
	s.requests[requestID] = advanceMemoryRecord{ProjectID: projectID, ClientID: clientID, Expected: expected, Next: next}
	s.heads[projectID] = next
	return next, nil
}

func (s *memoryAuthorityStore) RecordAttestation(_ context.Context, record AttestationRecord) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, prior := range s.attestations {
		if prior.ProjectID == record.ProjectID && prior.ExecutionID == record.ExecutionID && prior.EvidenceDigestSHA256 == record.EvidenceDigestSHA256 {
			return ErrConflict
		}
	}
	if record.ProjectID == "" || len(record.SignatureDER) == 0 {
		return errors.New("invalid attestation")
	}
	s.attestations = append(s.attestations, record)
	return nil
}
