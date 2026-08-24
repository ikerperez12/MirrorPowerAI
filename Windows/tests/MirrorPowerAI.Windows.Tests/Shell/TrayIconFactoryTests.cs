using System.Buffers.Binary;
using System.Drawing;
using MirrorPowerAI.Windows.Shell;

namespace MirrorPowerAI.Windows.Tests.Shell;

public sealed class TrayIconFactoryTests
{
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    [Fact]
    public void BuildIcoData_ContainsOrderedPngFramesForAllSupportedTraySizes()
    {
        var iconData = TrayIconFactory.BuildIcoData();
        var span = iconData.AsSpan();

        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(span[0..2]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[2..4]));
        Assert.Equal(
            checked((ushort)ExpectedSizes.Length),
            BinaryPrimitives.ReadUInt16LittleEndian(span[4..6]));

        var previousEnd = 6 + (16 * ExpectedSizes.Length);
        for (var index = 0; index < ExpectedSizes.Length; index++)
        {
            var entryOffset = 6 + (index * 16);
            var expectedEncodedSize = ExpectedSizes[index] == 256 ? 0 : ExpectedSizes[index];
            Assert.Equal(expectedEncodedSize, span[entryOffset]);
            Assert.Equal(expectedEncodedSize, span[entryOffset + 1]);
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[(entryOffset + 4)..(entryOffset + 6)]));
            Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(span[(entryOffset + 6)..(entryOffset + 8)]));

            var frameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                span[(entryOffset + 8)..(entryOffset + 12)]));
            var frameOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                span[(entryOffset + 12)..(entryOffset + 16)]));
            Assert.Equal(previousEnd, frameOffset);
            Assert.True(frameLength > 8);
            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], span.Slice(frameOffset, 8).ToArray());
            previousEnd = checked(frameOffset + frameLength);
        }

        Assert.Equal(iconData.Length, previousEnd);
    }

    [Fact]
    public void Create_ReturnsAUsableDetachedIcon()
    {
        using var stream = new MemoryStream(TrayIconFactory.BuildIcoData(), writable: false);
        using var generatedIcon = new Icon(stream);
        using var icon = TrayIconFactory.Create();

        Assert.NotEqual(nint.Zero, generatedIcon.Handle);
        Assert.NotEqual(nint.Zero, icon.Handle);
        Assert.True(icon.Width > 0);
        Assert.True(icon.Height > 0);
    }
}
