package httpgetauth

import (
	"bytes"
	"crypto/hkdf"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"net/url"
	"sort"
	"strings"
)

const Version = "2"
const salt = "amneziageo.http-get.v2"

var ErrFields = errors.New("http-get-auth-fields")
var ErrSignature = errors.New("http-get-auth-signature")
var ErrKey = errors.New("http-get-auth-key")

type Keys struct {
	id        [32]byte
	request   [32]byte
	response  [32]byte
	challenge [32]byte
}

type Stamp struct {
	Epoch [16]byte
	Time  uint64
	Nonce [16]byte
}

type Response struct {
	Status   int
	Epoch    [16]byte
	Time     uint64
	Version  string
	Sequence string
	Dropped  string
	Fault    string
	Body     []byte
}

func Derive(credential string) (Keys, error) {
	var result Keys
	credential = strings.ToLower(credential)
	if len(credential) != 36 {
		return result, ErrKey
	}
	for i, value := range []byte(credential) {
		separator := i == 8 || i == 13 || i == 18 || i == 23
		if separator && value != '-' || !separator && !(value >= '0' && value <= '9' || value >= 'a' && value <= 'f') {
			return result, ErrKey
		}
	}
	if credential == "00000000-0000-0000-0000-000000000000" {
		return result, ErrKey
	}
	result.id = sha256.Sum256([]byte("amneziageo.http-get.id.v2\x00" + credential))
	for purpose, target := range map[string]*[32]byte{"request": &result.request, "response": &result.response, "challenge": &result.challenge} {
		value, err := hkdf.Key(sha256.New, []byte(credential), []byte(salt), purpose, 32)
		if err != nil {
			return Keys{}, ErrKey
		}
		copy(target[:], value)
	}
	return result, nil
}

func (k Keys) ID() string { return hex.EncodeToString(k.id[:]) }

func put(buffer *bytes.Buffer, value []byte) {
	var length [4]byte
	binary.BigEndian.PutUint32(length[:], uint32(len(value)))
	buffer.Write(length[:])
	buffer.Write(value)
}

func number(buffer *bytes.Buffer, value uint64) {
	var data [8]byte
	binary.BigEndian.PutUint64(data[:], value)
	buffer.Write(data[:])
}

func tag(key [32]byte, value []byte) string {
	mac := hmac.New(sha256.New, key[:])
	mac.Write(value)
	return base64.StdEncoding.EncodeToString(mac.Sum(nil))
}

func check(key [32]byte, value []byte, signature string) error {
	decoded, err := base64.StdEncoding.Strict().DecodeString(signature)
	if err != nil || len(decoded) != sha256.Size || base64.StdEncoding.EncodeToString(decoded) != signature {
		return ErrSignature
	}
	mac := hmac.New(sha256.New, key[:])
	mac.Write(value)
	if !hmac.Equal(mac.Sum(nil), decoded) {
		return ErrSignature
	}
	return nil
}

func (k Keys) challengeMessage(path string, nonce, epoch [16]byte, tick uint64) []byte {
	var data bytes.Buffer
	put(&data, []byte("amneziageo.http-get.challenge.v2"))
	put(&data, []byte(path))
	put(&data, k.id[:])
	put(&data, nonce[:])
	put(&data, epoch[:])
	number(&data, tick)
	return data.Bytes()
}

func (k Keys) SignChallenge(path string, nonce, epoch [16]byte, tick uint64) string {
	return tag(k.challenge, k.challengeMessage(path, nonce, epoch, tick))
}

func (k Keys) VerifyChallenge(path string, nonce, epoch [16]byte, tick uint64, signature string) error {
	return check(k.challenge, k.challengeMessage(path, nonce, epoch, tick), signature)
}

func (k Keys) requestMessage(method, path string, query url.Values, payload []byte, stamp Stamp) ([]byte, error) {
	if method != "GET" || len(path) == 0 || path[0] != '/' || len(path) > 1024 || len(query) > 32 {
		return nil, ErrFields
	}
	names := make([]string, 0, len(query))
	for name, values := range query {
		if name == "" || len(name) > 64 || len(values) != 1 || len(values[0]) > 1024 {
			return nil, ErrFields
		}
		names = append(names, name)
	}
	sort.Strings(names)
	var data bytes.Buffer
	put(&data, []byte("amneziageo.http-get.request.v2"))
	put(&data, []byte(method))
	put(&data, []byte(path))
	put(&data, k.id[:])
	put(&data, stamp.Epoch[:])
	number(&data, stamp.Time)
	put(&data, stamp.Nonce[:])
	var count [2]byte
	binary.BigEndian.PutUint16(count[:], uint16(len(names)))
	data.Write(count[:])
	for _, name := range names {
		put(&data, []byte(name))
		put(&data, []byte(query[name][0]))
	}
	digest := sha256.Sum256(payload)
	put(&data, digest[:])
	return data.Bytes(), nil
}

func (k Keys) SignRequest(method, path string, query url.Values, payload []byte, stamp Stamp) ([32]byte, string, error) {
	data, err := k.requestMessage(method, path, query, payload, stamp)
	if err != nil {
		return [32]byte{}, "", err
	}
	return sha256.Sum256(data), tag(k.request, data), nil
}

func (k Keys) VerifyRequest(method, path string, query url.Values, payload []byte, stamp Stamp, signature string) ([32]byte, error) {
	data, err := k.requestMessage(method, path, query, payload, stamp)
	if err != nil {
		return [32]byte{}, err
	}
	return sha256.Sum256(data), check(k.request, data, signature)
}

func responseMessage(request [32]byte, response Response) ([]byte, error) {
	if response.Status < 100 || response.Status > 599 || len(response.Sequence) > 20 || len(response.Dropped) > 20 || len(response.Fault) > 64 {
		return nil, ErrFields
	}
	var data bytes.Buffer
	put(&data, []byte("amneziageo.http-get.response.v2"))
	put(&data, request[:])
	var status [2]byte
	binary.BigEndian.PutUint16(status[:], uint16(response.Status))
	data.Write(status[:])
	put(&data, response.Epoch[:])
	number(&data, response.Time)
	put(&data, []byte(response.Version))
	put(&data, []byte(response.Sequence))
	put(&data, []byte(response.Dropped))
	put(&data, []byte(response.Fault))
	digest := sha256.Sum256(response.Body)
	put(&data, digest[:])
	return data.Bytes(), nil
}

func (k Keys) SignResponse(request [32]byte, response Response) (string, error) {
	data, err := responseMessage(request, response)
	if err != nil {
		return "", err
	}
	return tag(k.response, data), nil
}

func (k Keys) VerifyResponse(request [32]byte, response Response, signature string) error {
	data, err := responseMessage(request, response)
	if err != nil {
		return err
	}
	return check(k.response, data, signature)
}

func Hex16(value string) ([16]byte, error) {
	var result [16]byte
	decoded, err := hex.DecodeString(value)
	if err != nil || len(decoded) != len(result) {
		return result, ErrFields
	}
	copy(result[:], decoded)
	return result, nil
}
