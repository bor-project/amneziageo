using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class LocalDohSslQueryTests
{
    [Theory]
    [InlineData(2u)]
    [InlineData(259u)]
    public void OnlyConfirmedAbsenceReturnsNull(uint code)
    {
        Assert.Null(LocalDohSslQuery.Read(IPAddress.Loopback, Query));

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            required = 0;
            return code;
        }
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(87u)]
    [InlineData(1168u)]
    public void QueryFailuresRemainErrors(uint code)
    {
        var error = Assert.Throws<Win32Exception>(() => LocalDohSslQuery.Read(IPAddress.Loopback, Query));
        Assert.Equal((int)code, error.NativeErrorCode);

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            required = 0;
            return code;
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    public void AnExactQueryUsesTheAddressAndReturnsItsAppId(string value)
    {
        var address = IPAddress.Parse(value);
        var expected = new IPEndPoint(address, 443).Serialize();
        var appId = Guid.NewGuid();
        var calls = 0;

        Assert.Equal(appId.ToString("B"), LocalDohSslQuery.Read(address, Query));
        Assert.Equal(2, calls);

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            calls++;
            Assert.Equal(0, input.QueryType);
            Assert.Equal(0u, input.Token);
            for (var at = 0; at < expected.Size; at++)
            {
                Assert.Equal(expected[at], Marshal.ReadByte(input.Address, at));
            }

            required = (uint)Marshal.SizeOf<LocalDohSslQuery.SslSet>();
            if (length == 0)
            {
                Assert.Equal(nint.Zero, output);
                return 122;
            }

            Assert.Equal(required, length);
            Marshal.StructureToPtr(new LocalDohSslQuery.SslSet
            {
                Parameters = new LocalDohSslQuery.SslParameters { AppId = appId },
            }, output, false);
            return 0;
        }
    }

    [Fact]
    public void AFailedRetryDoesNotTurnIntoAbsence()
    {
        var calls = 0;
        var error = Assert.Throws<Win32Exception>(() => LocalDohSslQuery.Read(IPAddress.Loopback, Query));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(2, calls);

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            calls++;
            required = (uint)Marshal.SizeOf<LocalDohSslQuery.SslSet>();
            return calls == 1 ? 122u : 5u;
        }
    }

    [Theory]
    [InlineData(122u)]
    [InlineData(234u)]
    public void AResizedRecordIsQueriedAgain(uint resize)
    {
        var calls = 0;
        var appId = Guid.NewGuid();
        Assert.Equal(appId.ToString("B"), LocalDohSslQuery.Read(IPAddress.Loopback, Query));
        Assert.Equal(3, calls);

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            calls++;
            required = (uint)Marshal.SizeOf<LocalDohSslQuery.SslSet>() + (calls > 1 ? 64u : 0u);
            if (length < required)
            {
                return resize;
            }

            Marshal.StructureToPtr(new LocalDohSslQuery.SslSet
            {
                Parameters = new LocalDohSslQuery.SslParameters { AppId = appId },
            }, output, false);
            return 0;
        }
    }

    [Fact]
    public void ABindingRemovedDuringTheQueryIsConfirmedAbsent()
    {
        var calls = 0;
        Assert.Null(LocalDohSslQuery.Read(IPAddress.Loopback, Query));
        Assert.Equal(2, calls);

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            calls++;
            required = (uint)Marshal.SizeOf<LocalDohSslQuery.SslSet>();
            return calls == 1 ? 122u : 2u;
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(1048577u)]
    public void InvalidBufferSizesRemainErrors(uint size)
    {
        Assert.Throws<InvalidDataException>(() => LocalDohSslQuery.Read(IPAddress.Loopback, Query));

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            required = size;
            return 122;
        }
    }

    [Fact]
    public void SuccessWithoutARecordRemainsAnError()
    {
        Assert.Throws<InvalidDataException>(() => LocalDohSslQuery.Read(IPAddress.Loopback, Query));

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            required = 0;
            return 0;
        }
    }

    [Fact]
    public void RepeatedBufferGrowthRemainsAnError()
    {
        var calls = 0;
        Assert.Throws<IOException>(() => LocalDohSslQuery.Read(IPAddress.Loopback, Query));
        Assert.Equal(4, calls);

        uint Query(ref LocalDohSslQuery.SslQuery input, nint output, uint length, out uint required)
        {
            calls++;
            required = length + (uint)Marshal.SizeOf<LocalDohSslQuery.SslSet>();
            return 122;
        }
    }

    [Fact]
    public void NativeLayoutPreservesPointerAlignment()
    {
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<LocalDohSslQuery.SslQuery>(nameof(LocalDohSslQuery.SslQuery.Address)).ToInt32());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<LocalDohSslQuery.SslSet>(nameof(LocalDohSslQuery.SslSet.Parameters)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8, Marshal.OffsetOf<LocalDohSslQuery.SslParameters>(nameof(LocalDohSslQuery.SslParameters.AppId)).ToInt32());
    }
}
