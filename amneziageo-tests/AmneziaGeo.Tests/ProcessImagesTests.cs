using System;
using System.Text;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A program that ends before its traffic is decided is named by the image held from its start, which is read out
/// of the payload the kernel reports the start with.
/// </summary>
public sealed class ProcessImagesTests
{
    private const int LabelOffset = 48;
    private const int CreatedOffset = 12;

    [Fact]
    public void AStartNamesItsImage()
    {
        var payload = Payload(15968, 134333429354425576, 1, "\\Device\\HarddiskVolume3\\Windows\\System32\\nslookup.exe");

        Assert.True(ProcessImages.TryRead(payload, out var pid, out var path, out var created));
        Assert.Equal(15968u, pid);
        Assert.Equal("\\Device\\HarddiskVolume3\\Windows\\System32\\nslookup.exe", path);
        Assert.Equal(134333429354425576, created);
    }

    [Fact]
    public void ALabelOfAnyLengthIsSteppedOver()
    {
        var payload = Payload(4242, 1, 5, "C:\\app\\app.exe");

        Assert.True(ProcessImages.TryRead(payload, out var pid, out var path, out _));
        Assert.Equal(4242u, pid);
        Assert.Equal("C:\\app\\app.exe", path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(60)]
    public void AShortPayloadNamesNothing(int length)
    {
        Assert.False(ProcessImages.TryRead(new byte[length], out _, out _, out _));
    }

    [Fact]
    public void SomethingOtherThanAPathNamesNothing()
    {
        var payload = Payload(4242, 1, 1, "not a path");

        Assert.False(ProcessImages.TryRead(payload, out _, out var path, out _));
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void TheImageOutlivesTheProgram()
    {
        var images = new ProcessImages();
        images.Remember(4242, "C:\\app\\app.exe", 7);

        Assert.True(images.TryGet(4242, out var held));
        Assert.Equal("C:\\app\\app.exe", held.Path);
        Assert.Equal(7, held.Created);
        Assert.False(images.TryGet(4243, out _));
    }

    [Fact]
    public void WhatWasHeldLongestGoesFirst()
    {
        var images = new ProcessImages();
        for (var pid = 1u; pid <= 9000; pid++)
        {
            images.Remember(pid, "C:\\app\\app.exe", pid);
        }

        Assert.False(images.TryGet(1, out _));
        Assert.True(images.TryGet(9000, out _));
    }

    // The payload of a start: the fixed fields, a mandatory label of the given subauthority count, then the image.
    private static byte[] Payload(uint pid, long created, byte subAuthorities, string image)
    {
        var label = 8 + (4 * subAuthorities);
        var name = Encoding.Unicode.GetBytes(image + "\0");
        var payload = new byte[LabelOffset + label + name.Length];
        BitConverter.GetBytes(pid).CopyTo(payload, 0);
        BitConverter.GetBytes(created).CopyTo(payload, CreatedOffset);
        payload[LabelOffset] = 1;
        payload[LabelOffset + 1] = subAuthorities;
        name.CopyTo(payload, LabelOffset + label);
        return payload;
    }
}
