package xhttpnative

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/httpgetauth"
)

const recoveryBudget = 45 * time.Second
const tlsHandshakeTimeout = 10 * time.Second
const requestTimeout = 15 * time.Second

var errCertificatePin = errors.New("http-get-certificate-pin")
var errCertificateMissing = errors.New("http-get-certificate-missing")
var errProtocol = errors.New("http-get-protocol-mismatch")

func retryable(status int, err error) bool {
	if errors.Is(err, httpgetauth.ErrSignature) || errors.Is(err, httpgetauth.ErrFields) || errors.Is(err, errAuthKey) || errors.Is(err, errAuthVersion) || errors.Is(err, errAuthEpoch) {
		return false
	}
	if errors.Is(err, errAuthClock) || errors.Is(err, errAuthReplay) {
		return true
	}
	if status != 0 && status != http.StatusOK && status != http.StatusNoContent {
		return status == http.StatusRequestTimeout || status == http.StatusTooManyRequests ||
			status == http.StatusInternalServerError || status == http.StatusBadGateway ||
			status == http.StatusServiceUnavailable || status == http.StatusGatewayTimeout
	}
	if errors.Is(err, context.Canceled) || errors.Is(err, ErrProtect) || errors.Is(err, errProtocol) ||
		errors.Is(err, errCertificatePin) || errors.Is(err, errCertificateMissing) {
		return false
	}
	var verification *tls.CertificateVerificationError
	var unknown x509.UnknownAuthorityError
	var hostname x509.HostnameError
	var invalid x509.CertificateInvalidError
	var record tls.RecordHeaderError
	if errors.As(err, &verification) || errors.As(err, &unknown) || errors.As(err, &hostname) ||
		errors.As(err, &invalid) || errors.As(err, &record) {
		return false
	}
	var network net.Error
	return errors.As(err, &network) || errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF)
}

func (h *Host) retry(operation string, values url.Values, payload []byte) ([]byte, uint64, int, error) {
	ctx, cancel := context.WithTimeout(h.ctx, recoveryBudget)
	defer cancel()
	delay := time.Second
	for attempt := 1; ; attempt++ {
		data, sequence, status, err := h.request(ctx, operation, values, payload)
		if err == nil {
			return data, sequence, status, nil
		}
		if h.ctx.Err() != nil {
			return nil, 0, 0, h.ctx.Err()
		}
		timedOut := func() ([]byte, uint64, int, error) {
			return nil, 0, 0, fmt.Errorf("http-get-%s-recovery-timeout: %w (%v)", operation, ctx.Err(), err)
		}
		if ctx.Err() != nil {
			return timedOut()
		}
		if !retryable(status, err) {
			return data, sequence, status, err
		}
		if h.diagnostic != nil {
			deadline, _ := ctx.Deadline()
			h.diagnostic(fmt.Sprintf("HTTP GET %s retry %d, %dms left: %v", operation, attempt, time.Until(deadline).Milliseconds(), err))
		}
		timer := time.NewTimer(delay)
		select {
		case <-timer.C:
		case <-ctx.Done():
			timer.Stop()
			if h.ctx.Err() != nil {
				return nil, 0, 0, h.ctx.Err()
			}
			return timedOut()
		}
		delay = min(delay*2, 5*time.Second)
	}
}
