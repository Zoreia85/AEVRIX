package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"os"
	"sync"
	"testing"
	"time"
)

func TestPostgresAuthorityStoreMonotonicCASAndReplay(t *testing.T) {
	store := openIntegrationStore(t)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	projectID := randomTestUUID(t)

	first := Head{EntryCount: 1, HeadHashSHA256: hashChar('a')}
	confirmed, err := store.AdvanceHead(ctx, projectID, "request-first-"+randomToken(t), EmptyHead, first, "client-test")
	if err != nil || confirmed != first {
		t.Fatalf("first CAS failed: confirmed=%+v err=%v", confirmed, err)
	}

	requestID := "request-idempotent-" + randomToken(t)
	second := Head{EntryCount: 2, HeadHashSHA256: hashChar('b')}
	if _, err := store.AdvanceHead(ctx, projectID, requestID, first, second, "client-test"); err != nil {
		t.Fatalf("second CAS failed: %v", err)
	}
	if replayed, err := store.AdvanceHead(ctx, projectID, requestID, first, second, "client-test"); err != nil || replayed != second {
		t.Fatalf("exact request-id replay was not idempotent: head=%+v err=%v", replayed, err)
	}
	forged := Head{EntryCount: 2, HeadHashSHA256: hashChar('c')}
	if _, err := store.AdvanceHead(ctx, projectID, requestID, first, forged, "client-test"); !errors.Is(err, ErrConflict) {
		t.Fatalf("modified request-id replay did not fail as conflict: %v", err)
	}

	loaded, err := store.LoadHead(ctx, projectID)
	if err != nil || loaded != second {
		t.Fatalf("authoritative head mismatch after idempotent sequence: head=%+v err=%v", loaded, err)
	}
}

func TestPostgresAuthorityStoreConcurrentForkHasExactlyOneWinner(t *testing.T) {
	store := openIntegrationStore(t)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	projectID := randomTestUUID(t)
	first := Head{EntryCount: 1, HeadHashSHA256: hashChar('d')}
	if _, err := store.AdvanceHead(ctx, projectID, "request-seed-"+randomToken(t), EmptyHead, first, "client-test"); err != nil {
		t.Fatalf("seed CAS failed: %v", err)
	}

	candidates := []Head{
		{EntryCount: 2, HeadHashSHA256: hashChar('e')},
		{EntryCount: 2, HeadHashSHA256: hashChar('f')},
	}
	start := make(chan struct{})
	results := make(chan error, len(candidates))
	var wg sync.WaitGroup
	for index, candidate := range candidates {
		wg.Add(1)
		go func(index int, candidate Head) {
			defer wg.Done()
			<-start
			_, err := store.AdvanceHead(
				ctx,
				projectID,
				"request-race-"+string(rune('a'+index))+"-"+randomToken(t),
				first,
				candidate,
				"client-test",
			)
			results <- err
		}(index, candidate)
	}
	close(start)
	wg.Wait()
	close(results)

	successes := 0
	conflicts := 0
	for err := range results {
		switch {
		case err == nil:
			successes++
		case errors.Is(err, ErrConflict):
			conflicts++
		default:
			t.Fatalf("concurrent CAS returned unexpected error class: %v", err)
		}
	}
	if successes != 1 || conflicts != 1 {
		t.Fatalf("fork race did not produce exactly one winner: successes=%d conflicts=%d", successes, conflicts)
	}

	loaded, err := store.LoadHead(ctx, projectID)
	if err != nil {
		t.Fatalf("load winning head: %v", err)
	}
	if loaded.EntryCount != 2 || (loaded.HeadHashSHA256 != candidates[0].HeadHashSHA256 && loaded.HeadHashSHA256 != candidates[1].HeadHashSHA256) {
		t.Fatalf("stored head is not either legitimate winner: %+v", loaded)
	}
}

func TestPostgresAuthorityStoreNonceAndAttestationUniqueness(t *testing.T) {
	store := openIntegrationStore(t)
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()

	nonce := "nonce-integration-" + randomToken(t)
	now := time.Now().UTC()
	if err := store.UseNonce(ctx, "client-test", nonce, now); err != nil {
		t.Fatalf("first nonce reservation failed: %v", err)
	}
	if err := store.UseNonce(ctx, "client-test", nonce, now); !errors.Is(err, ErrReplay) {
		t.Fatalf("nonce replay was not classified as replay: %v", err)
	}

	projectID := randomTestUUID(t)
	record := AttestationRecord{
		Nonce:                "attestation-" + randomToken(t),
		ProjectID:            projectID,
		RunID:                "run-integration",
		ExecutionID:          "exec-integration",
		EvidenceDigestSHA256: hashChar('1'),
		Head:                 Head{EntryCount: 3, HeadHashSHA256: hashChar('2')},
		KeyID:                "authority-key-test",
		IssuedAt:             now,
		ExpiresAt:            now.Add(5 * time.Minute),
		SignatureDER:         []byte{0x30, 0x01, 0x00},
		ClientID:             "client-test",
	}
	if err := store.RecordAttestation(ctx, record); err != nil {
		t.Fatalf("first attestation record failed: %v", err)
	}
	record.Nonce = "attestation-" + randomToken(t)
	if err := store.RecordAttestation(ctx, record); !errors.Is(err, ErrConflict) {
		t.Fatalf("duplicate evidence attestation was not classified as conflict: %v", err)
	}
}

func openIntegrationStore(t *testing.T) *PostgresAuthorityStore {
	t.Helper()
	databaseURL := os.Getenv("AEVRIX_AUTHORITY_TEST_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("AEVRIX_AUTHORITY_TEST_DATABASE_URL is not configured")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	store, err := OpenPostgresAuthorityStore(ctx, databaseURL)
	if err != nil {
		t.Fatalf("open PostgreSQL integration store: %v", err)
	}
	t.Cleanup(store.Close)
	return store
}

func randomTestUUID(t *testing.T) string {
	t.Helper()
	bytes := make([]byte, 16)
	if _, err := rand.Read(bytes); err != nil {
		t.Fatal(err)
	}
	bytes[6] = (bytes[6] & 0x0f) | 0x40
	bytes[8] = (bytes[8] & 0x3f) | 0x80
	hexValue := hex.EncodeToString(bytes)
	return hexValue[0:8] + "-" + hexValue[8:12] + "-" + hexValue[12:16] + "-" + hexValue[16:20] + "-" + hexValue[20:32]
}

func randomToken(t *testing.T) string {
	t.Helper()
	bytes := make([]byte, 8)
	if _, err := rand.Read(bytes); err != nil {
		t.Fatal(err)
	}
	return hex.EncodeToString(bytes)
}
