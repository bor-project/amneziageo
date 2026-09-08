using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The picker on Linux offers the installed applications as their desktop entries name them, so what an entry
/// says is read the way the rule is written: one program, by the path it stands at.
/// </summary>
public sealed class DesktopEntryTests
{
    [Fact]
    public void AnEntry_NamesTheProgramItStarts()
    {
        var entry = DesktopEntry.Read(
            ["[Desktop Entry]", "Type=Application", "Name=Firefox", "Exec=/usr/lib/firefox/firefox %u"], "en");

        Assert.NotNull(entry);
        Assert.Equal("Firefox", entry.Value.Name);
        Assert.Equal("/usr/lib/firefox/firefox", entry.Value.Image);
    }

    [Fact]
    public void AnEntry_TakesTheNameTheInterfaceLanguageAsksFor()
    {
        var lines = new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=Text Editor",
            "Name[ru]=Текстовый редактор",
            "Exec=/usr/bin/gedit",
        };

        Assert.Equal("Текстовый редактор", DesktopEntry.Read(lines, "ru")!.Value.Name);
        Assert.Equal("Text Editor", DesktopEntry.Read(lines, "en")!.Value.Name);
    }

    [Fact]
    public void AnEntry_PrefersTheProgramTryExecNames()
    {
        var entry = DesktopEntry.Read(
            ["[Desktop Entry]", "Type=Application", "Name=Steam", "TryExec=/usr/games/steam", "Exec=/usr/bin/steam %U"],
            "en");

        Assert.Equal("/usr/games/steam", entry!.Value.Image);
    }

    [Fact]
    public void AnEntry_StepsOverTheEnvironmentItIsStartedWith()
    {
        var entry = DesktopEntry.Read(
            ["[Desktop Entry]", "Type=Application", "Name=Signal", "Exec=env GDK_BACKEND=x11 /opt/Signal/signal-desktop"],
            "en");

        Assert.Equal("/opt/Signal/signal-desktop", entry!.Value.Image);
    }

    [Fact]
    public void AQuotedPath_StaysWhole()
    {
        var entry = DesktopEntry.Read(
            ["[Desktop Entry]", "Type=Application", "Name=Editor", "Exec=\"/opt/My Editor/bin/editor\" %F"], "en");

        Assert.Equal("/opt/My Editor/bin/editor", entry!.Value.Image);
    }

    [Fact]
    public void AnActionUnderTheEntry_IsNotReadAsOneOfItsOwn()
    {
        var entry = DesktopEntry.Read(
            [
                "[Desktop Entry]",
                "Type=Application",
                "Name=Browser",
                "Exec=/usr/bin/browser",
                "[Desktop Action new-window]",
                "Name=New window",
                "Exec=/usr/bin/browser --new-window",
            ],
            "en");

        Assert.Equal("/usr/bin/browser", entry!.Value.Image);
        Assert.Equal("Browser", entry.Value.Name);
    }

    [Theory]
    // Hidden from the menus of the machine, so it is hidden here too.
    [InlineData("Type=Application", "NoDisplay=true")]
    // A link and a directory start no program of their own.
    [InlineData("Type=Link", "URL=https://example.org")]
    // Every packaged application is started by this one launcher.
    [InlineData("Type=Application", "Exec=/usr/bin/flatpak run org.example.App")]
    public void WhatNamesNoProgramOfItsOwn_IsNotOffered(string kind, string tail)
    {
        Assert.Null(DesktopEntry.Read(["[Desktop Entry]", kind, "Name=Something", "Exec=/usr/bin/something", tail], "en"));
    }
}
