package httpgetauth

import (
	"bytes"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"net/url"
	"testing"
)

//go:embed testdata/vectors.json
var vectors []byte

type vector struct {
	Credential      string
	Path            string
	Query           map[string]string
	DataHex         string
	NonceHex        string
	EpochHex        string
	Time            uint64
	KeyId           string
	RequestKeyHex   string
	ResponseKeyHex  string
	ChallengeKeyHex string
	RequestHashHex  string
	RequestMac      string
	ResponseMac     string
	ChallengeMac    string
}

func fixture(t *testing.T) (vector, Keys, Stamp, url.Values, []byte) {
	t.Helper()
	var value vector
	if err := json.Unmarshal(vectors, &value); err != nil {
		t.Fatal(err)
	}
	keys, err := Derive(value.Credential)
	if err != nil {
		t.Fatal(err)
	}
	var stamp Stamp
	stamp.Nonce, err = Hex16(value.NonceHex)
	if err != nil {
		t.Fatal(err)
	}
	stamp.Epoch, err = Hex16(value.EpochHex)
	if err != nil {
		t.Fatal(err)
	}
	stamp.Time = value.Time
	query := make(url.Values)
	for name, item := range value.Query {
		query.Set(name, item)
	}
	data, err := hex.DecodeString(value.DataHex)
	if err != nil {
		t.Fatal(err)
	}
	return value, keys, stamp, query, data
}

func TestIndependentPythonVectors(t *testing.T) {
	v, keys, stamp, query, data := fixture(t)
	if keys.ID() != v.KeyId || hex.EncodeToString(keys.request[:]) != v.RequestKeyHex || hex.EncodeToString(keys.response[:]) != v.ResponseKeyHex || hex.EncodeToString(keys.challenge[:]) != v.ChallengeKeyHex {
		t.Fatal("key derivation differs")
	}
	digest, signature, err := keys.SignRequest("GET", v.Path, query, data, stamp)
	if err != nil || hex.EncodeToString(digest[:]) != v.RequestHashHex || signature != v.RequestMac {
		t.Fatal("request vector differs", err)
	}
	if _, err := keys.VerifyRequest("GET", v.Path, query, data, stamp, signature); err != nil {
		t.Fatal(err)
	}
	if signature := keys.SignChallenge(v.Path, stamp.Nonce, stamp.Epoch, stamp.Time); signature != v.ChallengeMac {
		t.Fatal("challenge vector differs")
	}
	if err := keys.VerifyChallenge(v.Path, stamp.Nonce, stamp.Epoch, stamp.Time, v.ChallengeMac); err != nil {
		t.Fatal(err)
	}
	response := Response{Status: 200, Epoch: stamp.Epoch, Time: 123499, Version: "2", Sequence: "7", Dropped: "12"}
	actual, err := keys.SignResponse(digest, response)
	if err != nil || actual != v.ResponseMac {
		t.Fatal("response vector differs", err)
	}
	if err := keys.VerifyResponse(digest, response, actual); err != nil {
		t.Fatal(err)
	}
}

func TestEveryRequestComponentIsBound(t *testing.T) {
	v, keys, stamp, query, data := fixture(t)
	mutations := []string{"method", "path", "epoch", "clock", "nonce", "data", "op", "seq", "s", "r", "target"}
	for _, mutation := range mutations {
		t.Run(mutation, func(t *testing.T) {
			changed := stamp
			params := make(url.Values)
			for key, values := range query {
				params[key] = append([]string(nil), values...)
			}
			method, path := "GET", v.Path
			payload := append([]byte(nil), data...)
			switch mutation {
			case "method":
				method = "POST"
			case "path":
				path = "/other/"
			case "epoch":
				changed.Epoch[0] ^= 1
			case "clock":
				changed.Time++
			case "nonce":
				changed.Nonce[0] ^= 1
			case "data":
				payload[0] ^= 1
			case "target":
				params.Set("host", "other.example")
			default:
				params.Set(mutation, params.Get(mutation)+"1")
			}
			if _, err := keys.VerifyRequest(method, path, params, payload, changed, v.RequestMac); err == nil {
				t.Fatal("modified request accepted")
			}
		})
	}
}

func TestEveryResponseComponentIsBound(t *testing.T) {
	v, keys, stamp, query, data := fixture(t)
	digest, _, _ := keys.SignRequest("GET", v.Path, query, data, stamp)
	for _, mutation := range []string{"request", "status", "epoch", "clock", "version", "sequence", "dropped", "fault", "body"} {
		t.Run(mutation, func(t *testing.T) {
			request := digest
			response := Response{Status: 200, Epoch: stamp.Epoch, Time: 123499, Version: "2", Sequence: "7", Dropped: "12"}
			switch mutation {
			case "request":
				request[0] ^= 1
			case "status":
				response.Status = 204
			case "epoch":
				response.Epoch[0] ^= 1
			case "clock":
				response.Time++
			case "version":
				response.Version = "1"
			case "sequence":
				response.Sequence = "8"
			case "dropped":
				response.Dropped = "0"
			case "fault":
				response.Fault = "clock"
			case "body":
				response.Body = []byte{1}
			}
			if err := keys.VerifyResponse(request, response, v.ResponseMac); err == nil {
				t.Fatal("modified response accepted")
			}
		})
	}
}

func TestAmbiguousQueriesAndNoncanonicalSignaturesAreRejected(t *testing.T) {
	v, keys, stamp, query, data := fixture(t)
	query.Add("op", "pull")
	if _, _, err := keys.SignRequest("GET", v.Path, query, data, stamp); err == nil {
		t.Fatal("duplicate query accepted")
	}
	for _, signature := range []string{v.ChallengeMac + "\n", v.ChallengeMac[:len(v.ChallengeMac)-1], "", "AA=="} {
		if err := keys.VerifyChallenge(v.Path, stamp.Nonce, stamp.Epoch, stamp.Time, signature); err == nil {
			t.Fatal("noncanonical signature accepted")
		}
	}
	if _, err := Derive("00000000-0000-0000-0000-000000000000"); err == nil {
		t.Fatal("empty key accepted")
	}
	if bytes.Equal(keys.request[:], keys.response[:]) || bytes.Equal(keys.request[:], keys.challenge[:]) {
		t.Fatal("signing purposes share a key")
	}
}
