using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeRevit.Services;

internal static class AttachmentImage
{
    public static byte[] EncodePng(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000) throw new InvalidDataException("Image exceeds 100 megapixels.");
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        input.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = input;
        if (longest > 1568) { if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = 1568; else image.DecodePixelHeight = 1568; }
        image.EndInit(); image.Freeze();
        BitmapSource source = image;
        while (true)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream(); encoder.Save(output);
            if (output.Length <= 5_000_000) return output.ToArray();
            source = new TransformedBitmap(source, new ScaleTransform(0.75, 0.75)); source.Freeze();
        }
    }
}
