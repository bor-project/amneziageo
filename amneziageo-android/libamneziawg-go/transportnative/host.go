package transportnative

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"strings"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/udppressure"
	"github.com/bor-project/amneziageo/libamneziawg-go/xhttpnative"
)

type Carrier interface {
	WaitReady(context.Context) error
	Wait() error
	Stop(context.Context) error
}

func Start(data []byte, sink udppressure.Sink) (Carrier, error) {
	p, err := Parse(data, false)
	if err != nil {
		return nil, err
	}
	if p.Transport == "http" {
		legacy, err := p.HTTPProfile()
		if err != nil {
			return nil, ErrProfile
		}
		return xhttpnative.StartWithPressure(legacy, sink)
	}
	return startQUIC(p, sink)
}

func Probe(ctx context.Context, data []byte) error {
	p, err := Parse(data, true)
	if err != nil {
		return err
	}
	connection, err := dialQUIC(ctx, p)
	if err == nil {
		connection.close()
	}
	return err
}

func FaultCode(err error) string {
	var fault proxyFault
	if errors.As(err, &fault) {
		return string(fault)
	}
	if errors.Is(err, ErrProfile) || errors.Is(err, xhttpnative.ErrProfile) {
		return "bad-proxy"
	}
	var verification *tls.CertificateVerificationError
	var unknown x509.UnknownAuthorityError
	var hostname x509.HostnameError
	var invalid x509.CertificateInvalidError
	if errors.As(err, &verification) || errors.As(err, &unknown) || errors.As(err, &hostname) || errors.As(err, &invalid) || strings.Contains(err.Error(), "certificate") {
		return "proxy-tls"
	}
	text := err.Error()
	switch {
	case strings.Contains(text, "open-status-403"):
		return "proxy-target-denied"
	case strings.Contains(text, "auth-key"), strings.Contains(text, "status-401"), strings.Contains(text, "status-403"):
		return "proxy-auth"
	case strings.Contains(text, "status-400"):
		return "proxy-target-denied"
	case strings.Contains(text, "version"), strings.Contains(text, "protocol-mismatch"), strings.Contains(text, "status-405"), strings.Contains(text, "signature"), strings.Contains(text, "server-restarted"):
		return "proxy-bad-answer"
	default:
		return "proxy-no-answer"
	}
}

type proxyFault string

func (e proxyFault) Error() string { return string(e) }
