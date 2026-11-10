package proxytls

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"math/big"
	"testing"
	"time"
)

func TestPinnedIdentityRequiresNameDatesAndServerUsage(t *testing.T) {
	for _, test := range []struct {
		name         string
		dns          string
		begins, ends time.Duration
		usage        x509.ExtKeyUsage
		wrong        bool
		accepted     bool
	}{
		{"trusted-pin", "proxy.example", -time.Hour, time.Hour, x509.ExtKeyUsageServerAuth, false, true},
		{"wrong-name", "attacker.example", -time.Hour, time.Hour, x509.ExtKeyUsageServerAuth, false, false},
		{"expired", "proxy.example", -2 * time.Hour, -time.Hour, x509.ExtKeyUsageServerAuth, false, false},
		{"future", "proxy.example", time.Hour, 2 * time.Hour, x509.ExtKeyUsageServerAuth, false, false},
		{"client-certificate", "proxy.example", -time.Hour, time.Hour, x509.ExtKeyUsageClientAuth, false, false},
		{"wrong-key", "proxy.example", -time.Hour, time.Hour, x509.ExtKeyUsageServerAuth, true, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
			if err != nil {
				t.Fatal(err)
			}
			now := time.Now()
			template := &x509.Certificate{SerialNumber: big.NewInt(1), DNSNames: []string{test.dns}, NotBefore: now.Add(test.begins), NotAfter: now.Add(test.ends), ExtKeyUsage: []x509.ExtKeyUsage{test.usage}}
			der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
			if err != nil {
				t.Fatal(err)
			}
			leaf, err := x509.ParseCertificate(der)
			if err != nil {
				t.Fatal(err)
			}
			digest := sha256.Sum256(der)
			if test.wrong {
				digest[0] ^= 1
			}
			config, err := Config("proxy.example", hex.EncodeToString(digest[:]), tls.VersionTLS12, nil)
			if err != nil {
				t.Fatal(err)
			}
			err = config.VerifyConnection(tls.ConnectionState{PeerCertificates: []*x509.Certificate{leaf}})
			if (err == nil) != test.accepted {
				t.Fatalf("accepted=%v, error=%v", test.accepted, err)
			}
			if config.VerifyConnection(tls.ConnectionState{}) == nil {
				t.Fatal("missing certificate accepted")
			}
		})
	}
}

func TestPublicPKIUsesStandardVerification(t *testing.T) {
	config, err := Config("proxy.example", "", tls.VersionTLS13, []string{"h3"})
	if err != nil || config.InsecureSkipVerify || config.VerifyConnection != nil || config.ServerName != "proxy.example" {
		t.Fatal("public PKI verification changed", err)
	}
}
