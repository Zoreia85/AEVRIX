package main

import (
	"context"
	"crypto/subtle"
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

func main() {
	if err := run(); err != nil {
		log.Printf("execution authority terminated: %v", sanitizeRuntimeError(err))
		os.Exit(1)
	}
}

func run() error {
	config, err := LoadAuthorityConfig()
	if err != nil {
		return err
	}
	defer zeroBytes(config.ClientSecret)

	startupCtx, startupCancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer startupCancel()
	store, err := OpenPostgresAuthorityStore(startupCtx, config.DatabaseURL)
	if err != nil {
		return err
	}
	defer store.Close()

	signer, err := NewPromotionSigner(config.SigningKeyID, config.SigningPrivateKey, config.AttestationLifetime)
	if err != nil {
		return err
	}
	authenticator, err := NewAuthorityAuthenticator(
		config.ClientID,
		config.ClientSecret,
		config.MaxClockSkew,
		store,
		time.Now,
	)
	if err != nil {
		return err
	}
	server, err := NewAuthorityServer(store, signer, authenticator, time.Now)
	if err != nil {
		return err
	}

	httpServer := &http.Server{
		Addr:              ":" + config.Port,
		Handler:           server.Handler(),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       10 * time.Second,
		WriteTimeout:      15 * time.Second,
		IdleTimeout:       60 * time.Second,
		MaxHeaderBytes:    16 * 1024,
	}

	shutdownSignal, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	errCh := make(chan error, 1)
	go func() {
		log.Printf("AEVRIX Execution Authority listening on configured port; signing key id=%s", config.SigningKeyID)
		if err := httpServer.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			errCh <- err
			return
		}
		errCh <- nil
	}()

	select {
	case err := <-errCh:
		return err
	case <-shutdownSignal.Done():
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := httpServer.Shutdown(shutdownCtx); err != nil {
			return fmt.Errorf("graceful authority shutdown failed: %w", err)
		}
		return <-errCh
	}
}

func zeroBytes(value []byte) {
	for i := range value {
		value[i] = 0
	}
	if len(value) > 0 {
		_ = subtle.ConstantTimeByteEq(value[0], 0)
	}
}

func sanitizeRuntimeError(err error) error {
	if err == nil {
		return nil
	}
	// Runtime configuration errors intentionally avoid including environment-variable values.
	// Database driver errors may otherwise echo the connection URL through wrapped messages, so
	// production logs keep only a stable class at this outermost process boundary.
	return errors.New("service startup or runtime failure; inspect protected Render diagnostics")
}
