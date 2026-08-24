using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MirrorPowerAI.Windows.Shell;

/// <summary>
/// Creates the application-owned, multi-resolution notification-area icon without loading an
/// opaque binary asset from disk.
/// </summary>
internal static class TrayIconFactory
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private const int IconDirectoryHeaderBytes = 6;
    private const int IconDirectoryEntryBytes = 16;

    /// <summary>
    /// Creates an icon detached from its temporary in-memory ICO stream. If GDI+ cannot render the
    /// application glyph, the caller still receives a usable Windows-owned fallback.
    /// </summary>
    internal static Icon Create()
    {
        try
        {
            using var stream = new MemoryStream(BuildIcoData(), writable: false);
            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    /// <summary>Builds a bounded ICO containing every size Windows commonly requests from the tray.</summary>
    internal static byte[] BuildIcoData()
    {
        var frames = IconSizes.Select(RenderPngFrame).ToArray();
        try
        {
            var headerBytes = checked(IconDirectoryHeaderBytes + (IconDirectoryEntryBytes * frames.Length));
            var totalBytes = frames.Aggregate(
                headerBytes,
                static (total, frame) => checked(total + frame.Length));
            var iconData = new byte[totalBytes];
            var span = iconData.AsSpan();

            BinaryPrimitives.WriteUInt16LittleEndian(span[0..2], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..4], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], checked((ushort)frames.Length));

            var imageOffset = headerBytes;
            for (var index = 0; index < frames.Length; index++)
            {
                var size = IconSizes[index];
                var entryOffset = IconDirectoryHeaderBytes + (index * IconDirectoryEntryBytes);
                span[entryOffset] = size == 256 ? (byte)0 : checked((byte)size);
                span[entryOffset + 1] = size == 256 ? (byte)0 : checked((byte)size);
                span[entryOffset + 2] = 0;
                span[entryOffset + 3] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(span[(entryOffset + 4)..(entryOffset + 6)], 1);
                BinaryPrimitives.WriteUInt16LittleEndian(span[(entryOffset + 6)..(entryOffset + 8)], 32);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(entryOffset + 8)..(entryOffset + 12)],
                    checked((uint)frames[index].Length));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(entryOffset + 12)..(entryOffset + 16)],
                    checked((uint)imageOffset));
                frames[index].CopyTo(span[imageOffset..]);
                imageOffset = checked(imageOffset + frames[index].Length);
            }

            return iconData;
        }
        finally
        {
            foreach (var frame in frames)
            {
                Array.Clear(frame);
            }
        }
    }

    private static byte[] RenderPngFrame(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var inset = Math.Max(1f, size * 0.055f);
        var diameter = size - (inset * 2);
        using var background = new SolidBrush(Color.FromArgb(255, 16, 27, 48));
        graphics.FillEllipse(background, inset, inset, diameter, diameter);

        var outerWidth = Math.Max(1.25f, size * 0.095f);
        using var outline = new Pen(Color.FromArgb(255, 93, 230, 255), outerWidth);
        graphics.DrawEllipse(
            outline,
            inset + (outerWidth / 2),
            inset + (outerWidth / 2),
            diameter - outerWidth,
            diameter - outerWidth);

        var powerWidth = Math.Max(1.35f, size * 0.105f);
        using var powerPen = new Pen(Color.White, powerWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        var center = size / 2f;
        graphics.DrawLine(powerPen, center, size * 0.19f, center, size * 0.49f);
        graphics.DrawArc(
            powerPen,
            size * 0.27f,
            size * 0.31f,
            size * 0.46f,
            size * 0.46f,
            -42,
            264);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
