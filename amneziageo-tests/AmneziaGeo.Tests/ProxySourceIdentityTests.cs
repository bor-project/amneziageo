using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class ProxySourceIdentityTests
{
    private const string Source = "ecb5b437-921c-414c-8458-900f1bb1021b";
    private static readonly string Issuer = Convert.ToBase64String(SHA256.HashData("issuer"u8));

    [Fact]
    public void AValidSourceBindingReturnsTheCanonicalSpki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);

        Assert.True(ProxySourceIdentity.Verify(catalog, Issuer, out var actual));
        Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), actual);
    }

    [Fact]
    public void PublicKeyEncodingWhitespaceDoesNotCreateAnotherPinnedIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);
        var spaced = catalog with { Proof = catalog.Proof! with { PublicKey = catalog.Proof!.PublicKey.Insert(4, " \r\n") } };

        Assert.True(ProxySourceIdentity.Verify(spaced, Issuer, out var actual));
        Assert.Equal(catalog.Proof.PublicKey, actual);
    }

    [Fact]
    public void ABindingCannotBeReusedForAnotherAuthenticatedWgIssuer()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);
        var other = Convert.ToBase64String(SHA256.HashData("another-issuer"u8));

        Assert.False(ProxySourceIdentity.Verify(catalog, other, out var actual));
        Assert.Equal("", actual);
        Assert.False(ProxySourceIdentity.Verify(catalog with { Proof = catalog.Proof! with { Issuer = other } }, other, out _));
    }

    [Fact]
    public void TheSourceAndDomainArePartOfTheSignedBinding()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);
        Assert.False(ProxySourceIdentity.Verify(catalog with { Source = Guid.NewGuid().ToString("D") }, Issuer, out _));

        var wrongDomain = Signed(key, "other-protocol.v1");
        Assert.False(ProxySourceIdentity.Verify(wrongDomain, Issuer, out _));
    }

    [Fact]
    public void EquivalentUuidTextVerifiesAgainstTheCanonicalBinding()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);

        Assert.True(ProxySourceIdentity.Verify(catalog with { Source = "{" + Source.ToUpperInvariant() + "}" }, Issuer, out _));
    }

    [Fact]
    public void TrailingSpkiDataAndOtherCurvesAreRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);
        var trailing = catalog with
        {
            Proof = catalog.Proof! with { PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo().Append((byte)0).ToArray()) },
        };
        Assert.False(ProxySourceIdentity.Verify(trailing, Issuer, out _));

        using var otherCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var other = catalog with { Proof = catalog.Proof! with { PublicKey = Convert.ToBase64String(otherCurve.ExportSubjectPublicKeyInfo()) } };
        Assert.False(ProxySourceIdentity.Verify(other, Issuer, out _));
    }

    [Fact]
    public void DerSignaturesAreNotAcceptedAsP1363()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);
        var message = Message("amneziageo.proxy-source.v1");
        var der = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.False(ProxySourceIdentity.Verify(catalog with { Proof = catalog.Proof! with { Signature = Convert.ToBase64String(der) } }, Issuer, out _));
    }

    [Theory]
    [InlineData("!")]
    [InlineData("")]
    [InlineData("YQ==")]
    public void InvalidPublicKeysAndSignaturesReturnFailure(string malformed)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);

        Assert.False(ProxySourceIdentity.Verify(catalog with { Proof = catalog.Proof! with { PublicKey = malformed } }, Issuer, out _));
        Assert.False(ProxySourceIdentity.Verify(catalog with { Proof = catalog.Proof! with { Signature = malformed } }, Issuer, out _));
    }

    [Fact]
    public void EmptySourcesUnsignedCatalogsAndNoncanonicalIssuersCannotVerify()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Signed(key);
        Assert.False(ProxySourceIdentity.Verify(catalog with { Source = Guid.Empty.ToString("D") }, Issuer, out _));
        Assert.False(ProxySourceIdentity.Verify(catalog with { Proof = null }, Issuer, out _));
        var spacedIssuer = Issuer.Insert(4, " ");
        Assert.False(ProxySourceIdentity.Verify(catalog with { Proof = catalog.Proof! with { Issuer = spacedIssuer } }, spacedIssuer, out _));
    }

    [Fact]
    public void EffectiveNamespacesSeparateSignedUnsignedAndLegacyIdentities()
    {
        var signed = ProxySourceIdentity.Signed(Source);
        var unsigned = ProxySourceIdentity.Unsigned(Source, Issuer);
        var legacy = ProxySourceIdentity.Legacy(Issuer);
        var otherIssuer = Convert.ToBase64String(SHA256.HashData("another-issuer"u8));

        Assert.Equal(3, new[] { signed, unsigned, legacy }.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(Source, signed);
        Assert.NotEqual(unsigned, ProxySourceIdentity.Unsigned(Source, otherIssuer));
        Assert.NotEqual(legacy, ProxySourceIdentity.Legacy(otherIssuer));
        Assert.NotEqual(unsigned, ProxySourceIdentity.Signed(unsigned));
        Assert.NotEqual(legacy, ProxySourceIdentity.Signed(legacy));
        Assert.Equal(signed, ProxySourceIdentity.Signed(Source));
    }

    private static byte[] Message(string domain) => Encoding.UTF8.GetBytes(domain + "\n" + Source + "\n" + Issuer);

    private static OfferedProxyCatalog Signed(ECDsa key, string domain = "amneziageo.proxy-source.v1")
    {
        var signature = key.SignData(Message(domain), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new OfferedProxyCatalog(1, Source, [], new OfferedProxySourceProof(
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Issuer, Convert.ToBase64String(signature)));
    }
}
