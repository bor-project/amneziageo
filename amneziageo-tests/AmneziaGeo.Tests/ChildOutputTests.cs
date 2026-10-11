using System.Diagnostics;

using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The output of a started program is read in the encoding the program writes it in: UTF-8 for the programs shipped
/// with the application, the OEM code page of the system for its console programs.
/// </summary>
public sealed class ChildOutputTests
{
    // What a console program of a Russian system says of a service that does not run.
    private const string Said = "Служба не запущена.";

    // The same words as that program writes them into a pipe, in the code page 866.
    private static readonly byte[] _written = [0x91, 0xAB, 0xE3, 0xA6, 0xA1, 0xA0, 0x20, 0xAD, 0xA5, 0x20, 0xA7, 0xA0, 0xAF, 0xE3, 0xE9, 0xA5, 0xAD, 0xA0, 0x2E];

    [Fact]
    public void TheOutputOfASystemProgram_IsReadInTheOemPageOfTheSystem()
    {
        var start = ChildOutput.OfSystem(Redirected("sc.exe"), 866);

        Assert.Equal(866, start.StandardOutputEncoding?.CodePage);
        Assert.Equal(866, start.StandardErrorEncoding?.CodePage);
        Assert.Equal(Said, start.StandardOutputEncoding!.GetString(_written));
    }

    [Fact]
    public void ThePageAServiceReadItsProgramsIn_GarblesThatOutput()
    {
        var read = ChildOutput.Page(1251)!.GetString(_written);

        Assert.NotEqual(Said, read);
        Assert.Equal(Said.Length, read.Length);
    }

    [Fact]
    public void TheOutputOfAProgramOfTheApplication_IsReadAsUtf8()
    {
        var start = ChildOutput.Ours(Redirected("wstunnel.exe"));

        Assert.Equal(65001, start.StandardOutputEncoding?.CodePage);
        Assert.Equal(65001, start.StandardErrorEncoding?.CodePage);
        Assert.Empty(start.StandardOutputEncoding!.GetPreamble());
        Assert.Equal(Said, start.StandardOutputEncoding.GetString(System.Text.Encoding.UTF8.GetBytes(Said)));
    }

    [Fact]
    public void ASystemThatWritesUtf8_IsReadAsUtf8()
    {
        var start = ChildOutput.OfSystem(Redirected("netsh"), 65001);

        Assert.Equal(65001, start.StandardOutputEncoding?.CodePage);
        Assert.Equal(65001, start.StandardErrorEncoding?.CodePage);
    }

    [Fact]
    public void OnlyWhatIsRedirected_GetsAnEncoding()
    {
        var start = ChildOutput.OfSystem(new ProcessStartInfo("netsh") { RedirectStandardOutput = true }, 866);

        Assert.Equal(866, start.StandardOutputEncoding?.CodePage);
        Assert.Null(start.StandardErrorEncoding);
    }

    [Fact]
    public void APageNothingKnows_LeavesTheOutputAsItWasRead()
    {
        var start = ChildOutput.OfSystem(Redirected("netsh"), 99999);

        Assert.Null(start.StandardOutputEncoding);
        Assert.Null(start.StandardErrorEncoding);
    }

    private static ProcessStartInfo Redirected(string program)
    {
        return new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
    }
}
