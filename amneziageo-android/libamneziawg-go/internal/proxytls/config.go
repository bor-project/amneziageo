package proxytls

import (
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"errors"
)

var ErrIdentity = errors.New("proxy-tls")

func Config(name, pin string, minimum uint16, protocols []string) (*tls.Config, error) {
	config := &tls.Config{ServerName: name, MinVersion: minimum, NextProtos: protocols}
	if pin == "" {
		return config, nil
	}
	digest, err := hex.DecodeString(pin)
	if err != nil || len(digest) != sha256.Size || name == "" {
		return nil, ErrIdentity
	}
	config.InsecureSkipVerify = true
	config.VerifyConnection = func(state tls.ConnectionState) error {
		if len(state.PeerCertificates) == 0 {
			return ErrIdentity
		}
		leaf := state.PeerCertificates[0]
		actual := sha256.Sum256(leaf.Raw)
		if subtle.ConstantTimeCompare(digest, actual[:]) != 1 {
			return ErrIdentity
		}
		roots := x509.NewCertPool()
		roots.AddCert(leaf)
		_, err := leaf.Verify(x509.VerifyOptions{DNSName: name, Roots: roots, KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}})
		return err
	}
	return config, nil
}
