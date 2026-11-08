using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class HttpGetAuthenticationTests
{
    [Fact]
    public void IndependentVectorsMatchAndTamperingFails()
    {
        var auth = new HttpGetAuthentication("40168f45-37b1-4bfa-903c-9904f48489ae");
        Assert.Equal("acb90349082e8e5aca396946e91ff1d03f2fe46606a111384a9f37d9638be42a", auth.KeyId);
        var epoch = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var nonce = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        auth.VerifyChallenge("/native/", nonce, epoch, 123456, "ZSuP985Y9omG+i96EECv90e+XpzYQxCHh3tpmxG/n4c=");
        Assert.Throws<InvalidDataException>(() => auth.VerifyChallenge("/changed/", nonce, epoch, 123456, "ZSuP985Y9omG+i96EECv90e+XpzYQxCHh3tpmxG/n4c="));
        var query = new Dictionary<string, string>
        {
            ["op"] = "push", ["s"] = "0123456789abcdef0123456789abcdef",
            ["r"] = "00112233445566778899aabbccddeeff", ["seq"] = "7",
        };
        var (digest, signature) = auth.SignRequest("/native/", query, Convert.FromHexString("000301020300020405"), epoch, 123456, nonce);
        Assert.Equal("7306b51d140c96eaedd028435719ffed6c68aeba1f7726b45ca2872be173007d", Convert.ToHexStringLower(digest));
        Assert.Equal("U228VCG4+lJo6vm5IfcPKerYXqa88WVRWoYXCREWT6A=", signature);
        auth.VerifyResponse(digest, 200, epoch, 123499, "2", "7", "12", "", [], "ooJLpzEZTT7s55E9gGEqkcWCNOgfR4BuIYS/35iOc7o=");
        Assert.Throws<InvalidDataException>(() => auth.VerifyResponse(digest, 200, epoch, 123499, "2", "8", "12", "", [], "ooJLpzEZTT7s55E9gGEqkcWCNOgfR4BuIYS/35iOc7o="));
        Assert.Throws<InvalidDataException>(() => auth.VerifyResponse(digest, 200, epoch, 123499, "2", "7", "12", "", [1], "ooJLpzEZTT7s55E9gGEqkcWCNOgfR4BuIYS/35iOc7o="));
        Assert.Throws<InvalidDataException>(() => auth.VerifyResponse(digest, 200, epoch, 123499, "2", "7", "12", "", [], "ooJLpzEZTT7s55E9gGEqkcWCNOgfR4BuIYS/35iOc7o=\n"));
    }
}
