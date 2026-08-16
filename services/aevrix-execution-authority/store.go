package main

import (
	"context"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

var (
	ErrHeadNotFound = errors.New("execution authority head not found")
	ErrConflict     = errors.New("execution authority conflict")
	ErrReplay       = errors.New("execution authority request replay")
)

type AttestationRecord struct {
	Nonce                string
	ProjectID            string
	RunID                string
	ExecutionID          string
	EvidenceDigestSHA256 string
	Head                 Head
	KeyID                string
	IssuedAt             time.Time
	ExpiresAt            time.Time
	SignatureDER         []byte
	ClientID             string
}

type AuthorityStore interface {
	Ping(context.Context) error
	UseNonce(context.Context, string, string, time.Time) error
	LoadHead(context.Context, string) (Head, error)
	AdvanceHead(context.Context, string, string, Head, Head, string) (Head, error)
	RecordAttestation(context.Context, AttestationRecord) error
}

type PostgresAuthorityStore struct {
	pool *pgxpool.Pool
}

func OpenPostgresAuthorityStore(ctx context.Context, databaseURL string) (*PostgresAuthorityStore, error) {
	config, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		return nil, fmt.Errorf("parse PostgreSQL configuration: %w", err)
	}
	config.MinConns = 1
	config.MaxConns = 8
	config.MaxConnLifetime = 30 * time.Minute
	config.MaxConnIdleTime = 5 * time.Minute
	config.HealthCheckPeriod = 30 * time.Second
	pool, err := pgxpool.NewWithConfig(ctx, config)
	if err != nil {
		return nil, fmt.Errorf("create PostgreSQL pool: %w", err)
	}
	store := &PostgresAuthorityStore{pool: pool}
	if err := store.ensureSchema(ctx); err != nil {
		pool.Close()
		return nil, err
	}
	if err := store.Ping(ctx); err != nil {
		pool.Close()
		return nil, err
	}
	return store, nil
}

func (s *PostgresAuthorityStore) Close() {
	if s != nil && s.pool != nil {
		s.pool.Close()
	}
}

func (s *PostgresAuthorityStore) Ping(ctx context.Context) error {
	if err := s.pool.Ping(ctx); err != nil {
		return fmt.Errorf("execution authority PostgreSQL ping failed: %w", err)
	}
	return nil
}

func (s *PostgresAuthorityStore) ensureSchema(ctx context.Context) error {
	statements := []string{
		`CREATE TABLE IF NOT EXISTS execution_authority_heads (
			project_id uuid PRIMARY KEY,
			entry_count bigint NOT NULL CHECK (entry_count > 0),
			head_hash char(64) NOT NULL CHECK (head_hash ~ '^[0-9a-f]{64}$'),
			version bigint NOT NULL DEFAULT 1,
			updated_at timestamptz NOT NULL DEFAULT now()
		)`,
		`CREATE TABLE IF NOT EXISTS execution_authority_head_events (
			request_id varchar(160) PRIMARY KEY,
			project_id uuid NOT NULL,
			client_id varchar(120) NOT NULL,
			expected_count bigint NOT NULL CHECK (expected_count >= 0),
			expected_hash char(64) NOT NULL CHECK (expected_hash ~ '^[0-9a-f]{64}$'),
			next_count bigint NOT NULL CHECK (next_count > 0),
			next_hash char(64) NOT NULL CHECK (next_hash ~ '^[0-9a-f]{64}$'),
			created_at timestamptz NOT NULL DEFAULT now(),
			UNIQUE(project_id, next_count),
			UNIQUE(project_id, next_hash)
		)`,
		`CREATE TABLE IF NOT EXISTS execution_authority_nonces (
			client_id varchar(120) NOT NULL,
			nonce varchar(128) NOT NULL,
			observed_at timestamptz NOT NULL,
			PRIMARY KEY(client_id, nonce)
		)`,
		`CREATE INDEX IF NOT EXISTS ix_execution_authority_nonces_observed_at
			ON execution_authority_nonces(observed_at)`,
		`CREATE TABLE IF NOT EXISTS execution_authority_attestations (
			nonce varchar(128) PRIMARY KEY,
			project_id uuid NOT NULL,
			run_id varchar(160) NOT NULL,
			execution_id varchar(160) NOT NULL,
			evidence_digest char(64) NOT NULL CHECK (evidence_digest ~ '^[0-9a-f]{64}$'),
			head_count bigint NOT NULL CHECK (head_count > 0),
			head_hash char(64) NOT NULL CHECK (head_hash ~ '^[0-9a-f]{64}$'),
			key_id varchar(120) NOT NULL,
			issued_at timestamptz NOT NULL,
			expires_at timestamptz NOT NULL,
			signature_der bytea NOT NULL,
			client_id varchar(120) NOT NULL,
			created_at timestamptz NOT NULL DEFAULT now(),
			UNIQUE(project_id, execution_id, evidence_digest)
		)`,
	}
	for _, statement := range statements {
		if _, err := s.pool.Exec(ctx, statement); err != nil {
			return fmt.Errorf("initialize execution authority schema: %w", err)
		}
	}
	return nil
}

func (s *PostgresAuthorityStore) UseNonce(ctx context.Context, clientID, nonce string, observedAt time.Time) error {
	if !safeToken(clientID, 3, 120) || !safeToken(nonce, 16, 128) {
		return errors.New("invalid nonce reservation")
	}
	_, _ = s.pool.Exec(ctx, `DELETE FROM execution_authority_nonces WHERE observed_at < now() - interval '15 minutes'`)
	tag, err := s.pool.Exec(ctx,
		`INSERT INTO execution_authority_nonces(client_id, nonce, observed_at) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING`,
		clientID, nonce, observedAt.UTC())
	if err != nil {
		return fmt.Errorf("reserve execution authority nonce: %w", err)
	}
	if tag.RowsAffected() != 1 {
		return ErrReplay
	}
	return nil
}

func (s *PostgresAuthorityStore) LoadHead(ctx context.Context, projectID string) (Head, error) {
	if !validUUID(projectID) {
		return Head{}, errors.New("invalid project id")
	}
	var head Head
	err := s.pool.QueryRow(ctx,
		`SELECT entry_count, head_hash FROM execution_authority_heads WHERE project_id = $1::uuid`,
		projectID).Scan(&head.EntryCount, &head.HeadHashSHA256)
	if errors.Is(err, pgx.ErrNoRows) {
		return Head{}, ErrHeadNotFound
	}
	if err != nil {
		return Head{}, fmt.Errorf("load execution authority head: %w", err)
	}
	head.HeadHashSHA256 = strings.ToLower(strings.TrimSpace(head.HeadHashSHA256))
	if err := validateHead(head, false); err != nil {
		return Head{}, fmt.Errorf("stored execution authority head is invalid: %w", err)
	}
	return head, nil
}

func (s *PostgresAuthorityStore) AdvanceHead(
	ctx context.Context,
	projectID string,
	requestID string,
	expected Head,
	next Head,
	clientID string,
) (Head, error) {
	if !validUUID(projectID) || !safeToken(requestID, 3, 160) || !safeToken(clientID, 3, 120) {
		return Head{}, errors.New("invalid execution authority CAS identity")
	}
	if err := validateHead(expected, true); err != nil {
		return Head{}, err
	}
	if err := validateHead(next, false); err != nil {
		return Head{}, err
	}
	if next.EntryCount != expected.EntryCount+1 {
		return Head{}, errors.New("execution authority CAS must advance exactly one record")
	}
	expected.HeadHashSHA256 = strings.ToLower(expected.HeadHashSHA256)
	next.HeadHashSHA256 = strings.ToLower(next.HeadHashSHA256)

	// The per-project transaction advisory lock is the serialization authority for this CAS.
	// READ COMMITTED is intentional: a contender that waited for the lock must observe the head
	// committed by the winner, then fail the exact-predecessor check deterministically.
	tx, err := s.pool.BeginTx(ctx, pgx.TxOptions{IsoLevel: pgx.ReadCommitted})
	if err != nil {
		return Head{}, fmt.Errorf("begin execution authority CAS transaction: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	lockKey := projectAdvisoryLockKey(projectID)
	if _, err := tx.Exec(ctx, `SELECT pg_advisory_xact_lock($1)`, lockKey); err != nil {
		if authorityConcurrencyError(err) {
			return Head{}, ErrConflict
		}
		return Head{}, fmt.Errorf("acquire execution authority project lock: %w", err)
	}

	var existingProject, existingClient, existingExpectedHash, existingNextHash string
	var existingExpectedCount, existingNextCount int64
	err = tx.QueryRow(ctx,
		`SELECT project_id::text, client_id, expected_count, expected_hash, next_count, next_hash
		 FROM execution_authority_head_events WHERE request_id = $1`, requestID).
		Scan(&existingProject, &existingClient, &existingExpectedCount, &existingExpectedHash, &existingNextCount, &existingNextHash)
	if err == nil {
		if !strings.EqualFold(existingProject, projectID) ||
			existingClient != clientID ||
			existingExpectedCount != expected.EntryCount ||
			!strings.EqualFold(strings.TrimSpace(existingExpectedHash), expected.HeadHashSHA256) ||
			existingNextCount != next.EntryCount ||
			!strings.EqualFold(strings.TrimSpace(existingNextHash), next.HeadHashSHA256) {
			return Head{}, ErrConflict
		}
		if err := tx.Commit(ctx); err != nil {
			if authorityConcurrencyError(err) {
				return Head{}, ErrConflict
			}
			return Head{}, fmt.Errorf("commit idempotent execution authority CAS: %w", err)
		}
		return next, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		if authorityConcurrencyError(err) {
			return Head{}, ErrConflict
		}
		return Head{}, fmt.Errorf("check execution authority request idempotency: %w", err)
	}

	current := EmptyHead
	var currentCount int64
	var currentHash string
	err = tx.QueryRow(ctx,
		`SELECT entry_count, head_hash FROM execution_authority_heads WHERE project_id = $1::uuid FOR UPDATE`, projectID).
		Scan(&currentCount, &currentHash)
	if err == nil {
		current = Head{EntryCount: currentCount, HeadHashSHA256: strings.ToLower(strings.TrimSpace(currentHash))}
	} else if !errors.Is(err, pgx.ErrNoRows) {
		if authorityConcurrencyError(err) {
			return Head{}, ErrConflict
		}
		return Head{}, fmt.Errorf("load execution authority CAS predecessor: %w", err)
	}
	if current != expected {
		return Head{}, ErrConflict
	}

	if _, err := tx.Exec(ctx,
		`INSERT INTO execution_authority_head_events
		 (request_id, project_id, client_id, expected_count, expected_hash, next_count, next_hash)
		 VALUES ($1, $2::uuid, $3, $4, $5, $6, $7)`,
		requestID, projectID, clientID, expected.EntryCount, expected.HeadHashSHA256, next.EntryCount, next.HeadHashSHA256); err != nil {
		if postgresErrorCode(err, "23505") || authorityConcurrencyError(err) {
			return Head{}, ErrConflict
		}
		return Head{}, fmt.Errorf("append execution authority head event: %w", err)
	}

	if _, err := tx.Exec(ctx,
		`INSERT INTO execution_authority_heads(project_id, entry_count, head_hash, version, updated_at)
		 VALUES ($1::uuid, $2, $3, 1, now())
		 ON CONFLICT (project_id) DO UPDATE SET
		 entry_count = EXCLUDED.entry_count,
		 head_hash = EXCLUDED.head_hash,
		 version = execution_authority_heads.version + 1,
		 updated_at = now()`,
		projectID, next.EntryCount, next.HeadHashSHA256); err != nil {
		if authorityConcurrencyError(err) {
			return Head{}, ErrConflict
		}
		return Head{}, fmt.Errorf("advance execution authority head: %w", err)
	}

	if err := tx.Commit(ctx); err != nil {
		if authorityConcurrencyError(err) {
			return Head{}, ErrConflict
		}
		return Head{}, fmt.Errorf("commit execution authority CAS: %w", err)
	}
	return next, nil
}

func (s *PostgresAuthorityStore) RecordAttestation(ctx context.Context, record AttestationRecord) error {
	if !safeToken(record.Nonce, 16, 128) || !validUUID(record.ProjectID) ||
		!safeToken(record.RunID, 3, 160) || !safeToken(record.ExecutionID, 3, 160) ||
		!validSHA256(record.EvidenceDigestSHA256) || validateHead(record.Head, false) != nil ||
		!safeToken(record.KeyID, 3, 120) || !safeToken(record.ClientID, 3, 120) ||
		record.IssuedAt.IsZero() || !record.ExpiresAt.After(record.IssuedAt) ||
		len(record.SignatureDER) == 0 || len(record.SignatureDER) > 2_048 {
		return errors.New("invalid execution authority attestation record")
	}
	_, err := s.pool.Exec(ctx,
		`INSERT INTO execution_authority_attestations
		 (nonce, project_id, run_id, execution_id, evidence_digest, head_count, head_hash, key_id, issued_at, expires_at, signature_der, client_id)
		 VALUES ($1, $2::uuid, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)`,
		record.Nonce, record.ProjectID, record.RunID, record.ExecutionID,
		strings.ToLower(record.EvidenceDigestSHA256), record.Head.EntryCount,
		strings.ToLower(record.Head.HeadHashSHA256), record.KeyID,
		record.IssuedAt.UTC(), record.ExpiresAt.UTC(), record.SignatureDER, record.ClientID)
	if err != nil {
		if postgresErrorCode(err, "23505") {
			return ErrConflict
		}
		return fmt.Errorf("record execution authority attestation: %w", err)
	}
	return nil
}

func postgresErrorCode(err error, code string) bool {
	var pgErr *pgconn.PgError
	return errors.As(err, &pgErr) && pgErr.Code == code
}

func authorityConcurrencyError(err error) bool {
	return postgresErrorCode(err, "40001") || postgresErrorCode(err, "40P01")
}

func projectAdvisoryLockKey(projectID string) int64 {
	digest := sha256.Sum256([]byte(strings.ToLower(projectID)))
	return int64(binary.BigEndian.Uint64(digest[:8]))
}
