using System;
using System.IO;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A per-app rule naming AmneziaGeo itself would send the agent's own downloads, the resolver upstream and the
/// websocket carrier into the tunnel they run, so such a rule is refused wherever it is written.
/// </summary>
public sealed class OwnAppRuleTests
{
    [Theory]
    [InlineData("app:path=C:\\Program Files\\AmneziaGeo\\AmneziaGeo.Windows.App.exe")]
    [InlineData("app:path=C:\\Program Files\\AmneziaGeo\\AmneziaGeo.Windows.Tray.exe")]
    [InlineData("app:path=D:\\portable\\amneziageo.exe")]
    [InlineData("app:path=C:/Program Files/AmneziaGeo/AmneziaGeo.Windows.Ui.exe")]
    [InlineData("app:svc=AmneziaGeoAgent")]
    [InlineData("app:svc=AmneziaGeo$bor-winlp-lv")]
    public void OwnImagesAndServicesAreNamed(string token)
    {
        Assert.True(OwnAppRule.Names(token));
    }

    [Theory]
    [InlineData("app:path=C:\\Program Files\\Telegram Desktop\\Telegram.exe")]
    [InlineData("app:dir=%APPDATA%\\Telegram Desktop")]
    [InlineData("app:svc=Dnscache")]
    [InlineData("app:pkg=5319275A.WhatsAppDesktop_cv1g1gvanyjgm")]
    [InlineData("geosite:youtube")]
    [InlineData("cidr:10.0.0.0/8")]
    public void OtherRulesAreNot(string token)
    {
        Assert.False(OwnAppRule.Names(token));
    }

    [Fact]
    public void TheFolderThisRuns_FromIsNamed()
    {
        var own = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        Assert.True(OwnAppRule.Names("app:dir=" + own));
        Assert.True(OwnAppRule.Names("app:path=" + Path.Combine(own, "anything.exe")));
    }

    [Fact]
    public void AFolderHoldingItIsNamedToo()
    {
        var above = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

        Assert.NotNull(above);
        Assert.True(OwnAppRule.Names("app:dir=" + above));
    }
}
