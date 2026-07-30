using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The shared machine directory carries geo bases and logs, never a user library. A root that resolves to it has to
/// be rejected: builds that bound the data root there served the pre-per-user store left in ProgramData to everyone.
/// </summary>
public sealed class AppDataRootTests
{
    private static readonly string Machine =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AmneziaGeo");

    [Fact]
    public void MachineRootIsRecognized()
    {
        Assert.True(AppDataRoot.IsMachineRoot(Machine));
        Assert.True(AppDataRoot.IsMachineRoot(Machine + Path.DirectorySeparatorChar));
        Assert.True(AppDataRoot.IsMachineRoot(Machine.ToUpperInvariant()));
    }

    [Fact]
    public void UserRootIsNotMachineRoot()
    {
        Assert.False(AppDataRoot.IsMachineRoot(AppDataRoot.UserBase(@"C:\Users\someone\AppData\Local")));
        Assert.False(AppDataRoot.IsMachineRoot(Path.Combine(Machine, "logs")));
    }

    [Fact]
    public void MissingRootIsNotMachineRoot()
    {
        Assert.False(AppDataRoot.IsMachineRoot(null));
        Assert.False(AppDataRoot.IsMachineRoot(string.Empty));
        Assert.False(AppDataRoot.IsMachineRoot("   "));
    }

    [Fact]
    public void BaseSitsUnderTheCurrentUserProfile()
    {
        var expected = AppDataRoot.UserBase(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Assert.Equal(expected, AppDataRoot.Base());
    }
}
