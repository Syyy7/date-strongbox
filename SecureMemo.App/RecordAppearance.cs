using System.Buffers.Binary;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using SecureMemo.Core;

namespace SecureMemo.App;

public sealed record IconChoice(string Name, string Label, string Glyph);

public sealed class RecordAppearance
{
    internal static readonly IconChoice[] Choices =
    [
        new("note", "笔记", "\uE70B"), new("lock", "锁", "\uE72E"),
        new("star", "星标", "\uE734"), new("globe", "网站", "\uE774"),
        new("mail", "邮件", "\uE715"), new("cloud", "云端", "\uE753"),
        new("folder", "文件夹", "\uE8B7"), new("heart", "收藏", "\uEB51")
    ];
    private const int Side = 64;
    internal IconChoice Choice { get; }
    internal Color Color { get; }
    public string Glyph => Choice.Glyph;
    public ImageSource? Image { get; }
    public Visibility SymbolVisibility => Image == null ? Visibility.Visible : Visibility.Collapsed;
    public SolidColorBrush Background { get; }
    public SolidColorBrush Foreground { get; }

    internal RecordAppearance(IconChoice choice, Color color, ImageSource? image)
    {
        Choice = choice; Color = color; Image = image;
        static double Linear(byte value)
        {
            double s = value / 255d;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        byte contrast = luminance > .179 ? (byte)0 : (byte)255;
        byte Tint(byte channel) => (byte)Math.Round(.12 * channel + .88 * contrast);
        var tile = image == null ? Color.FromArgb(255, Tint(color.R), Tint(color.G), Tint(color.B)) : color;
        Background = new SolidColorBrush(tile);
        Foreground = new SolidColorBrush(color);
    }

    internal static RecordAppearance From(Memo entry)
    {
        var defaultChoice = entry.Kind == "账号" ? Choices[1] : Choices[0];
        var choice = Array.Find(Choices, option => option.Name == entry.IconName) ?? defaultChoice;
        int rgb = entry.IconColor is >= 0 and <= 0xffffff ? entry.IconColor.Value :
            entry.Kind == "账号" ? 0x29665E : 0x515F91;
        var color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return new(choice, color, LoadImage(entry.IconPng));
    }

    internal static ImageSource? LoadImage(byte[]? png)
    {
        if (png is not { Length: >= 33 and <= 32768 } ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) is 0 or > Side ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) is 0 or > Side) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            using var source = stream.AsRandomAccessStream();
            var image = new BitmapImage();
            image.SetSource(source);
            return image;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.COMException)
        { return null; }
    }

    internal static async Task<byte[]> ImportAsync(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 8 * 1024 * 1024) throw new InvalidDataException("请选择 8 MiB 以内的图片。");
        using var source = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(source);
        var format = decoder.DecoderInformation.CodecId;
        if (format != BitmapDecoder.PngDecoderId && format != BitmapDecoder.JpegDecoderId)
            throw new InvalidDataException("请选择 PNG 或 JPEG 图片。");
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 32000000)
            throw new InvalidDataException("请选择 3200 万像素以内的图片。");
        double scale = Math.Min(1, (double)Side / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint Scaled(uint length) => (uint)Math.Max(1, Math.Round(length * scale));
        var transform = new BitmapTransform { ScaledWidth = Scaled(decoder.PixelWidth),
            ScaledHeight = Scaled(decoder.PixelHeight), InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, Scaled(decoder.OrientedPixelWidth),
            Scaled(decoder.OrientedPixelHeight), 96, 96, data.DetachPixelData());
        await encoder.FlushAsync();
        output.Seek(0);
        using var result = output.AsStreamForRead();
        byte[] png = new byte[checked((int)output.Size)];
        await result.ReadExactlyAsync(png);
        return png;
    }
}
