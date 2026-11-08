package xhttpnative

import (
	"context"
	"crypto/x509"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync/atomic"
	"syscall"
	"testing"
	"time"
)

type retryRoundTripper func(*http.Request) (*http.Response, error)

func (f retryRoundTripper) RoundTrip(request *http.Request) (*http.Response, error) {
	return f(request)
}

func retryHost(t *testing.T, ctx context.Context, send retryRoundTripper) *Host {
	auth, _ := newAuthentication(authFixture)
	auth.ready = true
	auth.received = time.Now()
	auth.epoch[0] = 1
	wrapped := retryRoundTripper(func(request *http.Request) (*http.Response, error) {
		response, err := send(request)
		if err != nil {
			return nil, err
		}
		return testSignedResponse(t, request, response), nil
	})
	return &Host{ctx: ctx, base: url.URL{Scheme: "https", Host: "example.invalid", Path: "/get/"},
		credential: "test-credential", authentication: auth, session: "test-session", client: &http.Client{Transport: wrapped}}
}

func retryResponse(status int) *http.Response {
	return &http.Response{StatusCode: status, Header: http.Header{"X-Wg-Version": {"1"}, "X-Wg-Seq": {"1"}}, Body: io.NopCloser(strings.NewReader(""))}
}

func transientRetryError() error {
	return &net.OpError{Op: "dial", Net: "tcp", Err: syscall.ECONNRESET}
}

func TestRetrySurvivesMoreThanThreeTransientFailures(t *testing.T) {
	t.Parallel()
	attempts := 0
	host := retryHost(t, context.Background(), func(request *http.Request) (*http.Response, error) {
		attempts++
		if request.Method != "GET" || request.Body != nil || request.Header.Get("X-WG-Data") != "AAEq" || request.URL.Query().Get("seq") != "1" || request.URL.Query().Get("s") != "test-session" {
			t.Error("retry changed the datagram or request envelope")
		}
		if attempts <= 4 {
			return nil, transientRetryError()
		}
		return retryResponse(http.StatusOK), nil
	})
	_, sequence, _, err := host.retry("push", url.Values{"seq": {"1"}}, []byte{0, 1, 42})
	if err != nil || sequence != 1 || attempts != 5 {
		t.Fatalf("retry did not preserve the session: attempts=%d sequence=%d err=%v", attempts, sequence, err)
	}
}

func TestRetryGatewayFailureDoesNotEndTheSession(t *testing.T) {
	t.Parallel()
	attempts := 0
	host := retryHost(t, context.Background(), func(*http.Request) (*http.Response, error) {
		attempts++
		if attempts == 1 {
			response := retryResponse(http.StatusServiceUnavailable)
			response.Header.Del("X-WG-Version")
			return response, nil
		}
		return retryResponse(http.StatusOK), nil
	})
	if _, _, _, err := host.retry("open", nil, nil); err != nil || attempts != 2 {
		t.Fatalf("gateway recovery failed: attempts=%d err=%v", attempts, err)
	}
}

func TestRetryRejectsPermanentFailuresImmediately(t *testing.T) {
	t.Parallel()
	cases := []struct {
		name   string
		status int
		err    error
	}{
		{"auth", http.StatusUnauthorized, nil},
		{"session-expired", http.StatusGone, nil},
		{"certificate", 0, x509.UnknownAuthorityError{Cert: &x509.Certificate{}}},
		{"pin", 0, errCertificatePin},
		{"protect", 0, ErrProtect},
	}
	for _, item := range cases {
		t.Run(item.name, func(t *testing.T) {
			attempts := 0
			host := retryHost(t, context.Background(), func(*http.Request) (*http.Response, error) {
				attempts++
				if item.err != nil {
					return nil, item.err
				}
				return retryResponse(item.status), nil
			})
			_, _, _, err := host.retry("open", nil, nil)
			if err == nil || attempts != 1 {
				t.Fatalf("permanent error was retried: attempts=%d err=%v", attempts, err)
			}
			if strings.Contains(err.Error(), "test-session") || strings.Contains(err.Error(), "test-credential") || strings.Contains(err.Error(), "https://") {
				t.Fatal("diagnostic exposed the request URL or credential")
			}
		})
	}
}

func TestRetryCancellationInterruptsTheBackoff(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	called := make(chan struct{}, 1)
	host := retryHost(t, ctx, func(*http.Request) (*http.Response, error) {
		select {
		case called <- struct{}{}:
		default:
		}
		return nil, transientRetryError()
	})
	done := make(chan error, 1)
	go func() { _, _, _, err := host.retry("open", nil, nil); done <- err }()
	<-called
	cancel()
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected cancellation: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("cancellation waited for the recovery budget")
	}
}

func TestRetryBudgetEventuallyExpires(t *testing.T) {
	t.Parallel()
	var attempts atomic.Int32
	host := retryHost(t, context.Background(), func(*http.Request) (*http.Response, error) {
		attempts.Add(1)
		return nil, transientRetryError()
	})
	started := time.Now()
	_, _, _, err := host.retry("open", nil, nil)
	if !errors.Is(err, context.DeadlineExceeded) || attempts.Load() <= 3 || time.Since(started) < recoveryBudget-time.Second {
		t.Fatalf("recovery budget was not enforced: attempts=%d elapsed=%v err=%v", attempts.Load(), time.Since(started), err)
	}
}
