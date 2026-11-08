package xhttpnative

import (
	"bytes"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"testing"
	"time"

	"github.com/bor-project/amneziageo/libamneziawg-go/internal/httpgetauth"
)

const authFixture = "40168f45-37b1-4bfa-903c-9904f48489ae"

func testSignedResponse(t *testing.T, request *http.Request, response *http.Response) *http.Response {
	t.Helper()
	keys, _ := httpgetauth.Derive(authFixture)
	if request.Header.Get("Authorization") != "" || request.Header.Get("X-WG-Key") != keys.ID() {
		t.Error("HTTP GET disclosed a credential or omitted the public identity")
	}
	epoch, _ := httpgetauth.Hex16(request.Header.Get("X-WG-Epoch"))
	nonce, _ := httpgetauth.Hex16(request.Header.Get("X-WG-Nonce"))
	tick, _ := strconv.ParseUint(request.Header.Get("X-WG-Time"), 10, 64)
	payload, _ := base64.StdEncoding.DecodeString(request.Header.Get("X-WG-Data"))
	digest, err := keys.VerifyRequest("GET", request.URL.EscapedPath(), request.URL.Query(), payload,
		httpgetauth.Stamp{Epoch: epoch, Nonce: nonce, Time: tick}, request.Header.Get("X-WG-Auth"))
	if err != nil {
		t.Error(err)
	}
	if response.Header.Get("X-WG-Version") == "" {
		return response
	}
	body, _ := io.ReadAll(response.Body)
	response.Body.Close()
	response.Body = io.NopCloser(bytes.NewReader(body))
	response.Header.Set("X-WG-Version", "2")
	response.Header.Set("X-WG-Epoch", hex.EncodeToString(epoch[:]))
	response.Header.Set("X-WG-Time", strconv.FormatUint(tick, 10))
	proof, err := keys.SignResponse(digest, httpgetauth.Response{Status: response.StatusCode, Epoch: epoch, Time: tick,
		Version: "2", Sequence: response.Header.Get("X-WG-Seq"), Dropped: response.Header.Get("X-WG-Down-Dropped"), Body: body})
	if err != nil {
		t.Fatal(err)
	}
	response.Header.Set("X-WG-Auth", proof)
	return response
}

func signedTestHandler(t *testing.T, handler http.Handler) http.Handler {
	t.Helper()
	keys, _ := httpgetauth.Derive(authFixture)
	var epoch [16]byte
	epoch[0] = 1
	start := time.Now()
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("op") == "challenge" {
			if r.Header.Get("Authorization") != "" || r.Header.Get("X-WG-Key") != keys.ID() {
				t.Error("challenge disclosed a credential")
				w.WriteHeader(401)
				return
			}
			nonce, err := httpgetauth.Hex16(r.URL.Query().Get("n"))
			if err != nil {
				w.WriteHeader(400)
				return
			}
			tick := uint64(time.Since(start).Milliseconds())
			w.Header().Set("X-WG-Version", "2")
			w.Header().Set("X-WG-Auth", keys.SignChallenge(r.URL.EscapedPath(), nonce, epoch, tick))
			json.NewEncoder(w).Encode(map[string]any{"epoch": hex.EncodeToString(epoch[:]), "time": tick})
			return
		}
		recorder := httptest.NewRecorder()
		handler.ServeHTTP(recorder, r)
		response := testSignedResponse(t, r, recorder.Result())
		defer response.Body.Close()
		for name, values := range response.Header {
			w.Header()[name] = values
		}
		w.WriteHeader(response.StatusCode)
		io.Copy(w, response.Body)
	})
}
