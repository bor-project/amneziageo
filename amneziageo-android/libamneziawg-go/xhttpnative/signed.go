package xhttpnative

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"sync"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/httpgetauth"
)

var errAuthClock = errors.New("http-get-auth-clock")
var errAuthReplay = errors.New("http-get-auth-replay")
var errAuthEpoch = errors.New("http-get-server-restarted")
var errAuthVersion = errors.New("http-get-auth-version-required")
var errAuthKey = errors.New("http-get-auth-key-rejected")

type authentication struct {
	mu       sync.Mutex
	keys     httpgetauth.Keys
	ready    bool
	epoch    [16]byte
	tick     uint64
	received time.Time
}

func newAuthentication(credential string) (*authentication, error) {
	keys, err := httpgetauth.Derive(credential)
	if err != nil {
		return nil, err
	}
	return &authentication{keys: keys}, nil
}

func single(headers http.Header, name string) (string, bool) {
	values := headers.Values(name)
	if len(values) != 1 {
		return "", false
	}
	return values[0], true
}

func optional(headers http.Header, name string) (string, bool) {
	values := headers.Values(name)
	if len(values) == 0 {
		return "", true
	}
	return single(headers, name)
}

func gateway(status int) bool {
	return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504
}

func requestError(operation string, err error) error {
	var wrapped *url.Error
	if errors.As(err, &wrapped) {
		err = wrapped.Err
	}
	return fmt.Errorf("http-get-%s: %w", operation, err)
}

func (h *Host) prepareAuthentication(ctx context.Context) (int, error) {
	if h.authentication == nil {
		return 0, errAuthKey
	}
	a := h.authentication
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.ready {
		return 0, nil
	}
	var nonce [16]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		return 0, err
	}
	address := h.base
	address.RawQuery = url.Values{"op": {"challenge"}, "n": {hex.EncodeToString(nonce[:])}}.Encode()
	request, err := http.NewRequestWithContext(ctx, "GET", address.String(), nil)
	if err != nil {
		return 0, errProtocol
	}
	request.Header.Set("X-WG-Key", a.keys.ID())
	request.Header.Set("X-WG-Version", httpgetauth.Version)
	request.Header.Set("Cache-Control", "no-store, no-cache")
	response, err := h.client.Do(request)
	if err != nil {
		return 0, requestError("challenge", err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(io.LimitReader(response.Body, 4097))
	if err != nil {
		return response.StatusCode, err
	}
	version, valid := single(response.Header, "X-WG-Version")
	if !valid || version != httpgetauth.Version {
		if gateway(response.StatusCode) {
			return response.StatusCode, fmt.Errorf("http-get-challenge-status-%d", response.StatusCode)
		}
		return response.StatusCode, errAuthVersion
	}
	if response.StatusCode != http.StatusOK {
		if response.StatusCode == 401 || response.StatusCode == 403 {
			return response.StatusCode, errAuthKey
		}
		return response.StatusCode, fmt.Errorf("http-get-challenge-status-%d", response.StatusCode)
	}
	if len(body) > 4096 {
		return response.StatusCode, errProtocol
	}
	var proof struct {
		Epoch string  `json:"epoch"`
		Time  *uint64 `json:"time"`
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&proof) != nil || decoder.Decode(new(any)) != io.EOF || proof.Time == nil {
		return response.StatusCode, httpgetauth.ErrSignature
	}
	epoch, err := httpgetauth.Hex16(proof.Epoch)
	if err != nil {
		return response.StatusCode, httpgetauth.ErrSignature
	}
	signature, valid := single(response.Header, "X-WG-Auth")
	if !valid || a.keys.VerifyChallenge(h.base.EscapedPath(), nonce, epoch, *proof.Time, signature) != nil {
		return response.StatusCode, httpgetauth.ErrSignature
	}
	a.epoch = epoch
	a.tick = *proof.Time
	a.received = time.Now()
	a.ready = true
	return response.StatusCode, nil
}

func (h *Host) signedRequest(ctx context.Context, operation string, values url.Values, payload []byte) ([]byte, uint64, int, error) {
	if status, err := h.prepareAuthentication(ctx); err != nil {
		return nil, 0, status, err
	}
	if values == nil {
		values = make(url.Values)
	}
	values.Set("op", operation)
	values.Set("s", h.session)
	var nonce [16]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		return nil, 0, 0, err
	}
	values.Set("r", hex.EncodeToString(nonce[:]))
	a := h.authentication
	a.mu.Lock()
	stamp := httpgetauth.Stamp{Epoch: a.epoch, Time: a.tick + uint64(time.Since(a.received).Milliseconds()), Nonce: nonce}
	a.mu.Unlock()
	address := h.base
	address.RawQuery = values.Encode()
	digest, signature, err := a.keys.SignRequest("GET", address.EscapedPath(), values, payload, stamp)
	if err != nil {
		return nil, 0, 0, err
	}
	request, err := http.NewRequestWithContext(ctx, "GET", address.String(), nil)
	if err != nil {
		return nil, 0, 0, errProtocol
	}
	request.Header.Set("X-WG-Version", httpgetauth.Version)
	request.Header.Set("X-WG-Key", a.keys.ID())
	request.Header.Set("X-WG-Epoch", hex.EncodeToString(stamp.Epoch[:]))
	request.Header.Set("X-WG-Time", strconv.FormatUint(stamp.Time, 10))
	request.Header.Set("X-WG-Nonce", hex.EncodeToString(nonce[:]))
	request.Header.Set("X-WG-Auth", signature)
	request.Header.Set("Cache-Control", "no-store, no-cache")
	request.Header.Set("Accept", "application/octet-stream")
	if payload != nil {
		request.Header.Set("X-WG-Data", base64.StdEncoding.EncodeToString(payload))
	}
	response, err := h.client.Do(request)
	if err != nil {
		return nil, 0, 0, requestError(operation, err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(io.LimitReader(response.Body, 65537))
	if err != nil {
		return nil, 0, response.StatusCode, err
	}
	if len(body) > 65536 {
		return nil, 0, response.StatusCode, errProtocol
	}
	version, valid := single(response.Header, "X-WG-Version")
	signature, signed := single(response.Header, "X-WG-Auth")
	if !valid || version != httpgetauth.Version || !signed {
		if gateway(response.StatusCode) {
			return nil, 0, response.StatusCode, fmt.Errorf("http-get-%s-status-%d", operation, response.StatusCode)
		}
		if !valid || version != httpgetauth.Version {
			return nil, 0, response.StatusCode, errAuthVersion
		}
		return nil, 0, response.StatusCode, httpgetauth.ErrSignature
	}
	epochValue, epochValid := single(response.Header, "X-WG-Epoch")
	timeValue, timeValid := single(response.Header, "X-WG-Time")
	epoch, epochError := httpgetauth.Hex16(epochValue)
	tick, timeError := strconv.ParseUint(timeValue, 10, 64)
	sequence, sequenceValid := single(response.Header, "X-WG-Seq")
	dropped, droppedValid := optional(response.Header, "X-WG-Down-Dropped")
	fault, faultValid := optional(response.Header, "X-WG-Auth-Error")
	if !epochValid || !timeValid || epochError != nil || timeError != nil || !sequenceValid || !droppedValid || !faultValid {
		return nil, 0, response.StatusCode, httpgetauth.ErrSignature
	}
	message := httpgetauth.Response{Status: response.StatusCode, Epoch: epoch, Time: tick, Version: version, Sequence: sequence, Dropped: dropped, Fault: fault, Body: body}
	if err := a.keys.VerifyResponse(digest, message, signature); err != nil {
		return nil, 0, response.StatusCode, err
	}
	if epoch != stamp.Epoch {
		return nil, 0, response.StatusCode, errAuthEpoch
	}
	if fault != "" {
		switch {
		case fault == "clock" && response.StatusCode == 409:
			a.mu.Lock()
			a.tick = tick
			a.received = time.Now()
			a.mu.Unlock()
			return nil, 0, response.StatusCode, errAuthClock
		case fault == "replay" && response.StatusCode == 409:
			return nil, 0, response.StatusCode, errAuthReplay
		case fault == "busy" && response.StatusCode == 503:
			return nil, 0, response.StatusCode, errors.New("http-get-auth-busy")
		case fault == "epoch":
			return nil, 0, response.StatusCode, errAuthEpoch
		default:
			return nil, 0, response.StatusCode, errProtocol
		}
	}
	if response.StatusCode != http.StatusOK && response.StatusCode != http.StatusNoContent {
		return nil, 0, response.StatusCode, fmt.Errorf("http-get-%s-status-%d", operation, response.StatusCode)
	}
	if operation == "open" && h.diagnostic != nil {
		h.diagnostic("HTTP GET protocol " + response.Proto)
	}
	number, err := strconv.ParseUint(sequence, 10, 64)
	return body, number, response.StatusCode, err
}
