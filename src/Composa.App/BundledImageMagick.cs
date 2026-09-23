using Magick = ImageMagick;

namespace Composa.App;

/// <summary>
/// ImageMagick as a library, through Magick.NET, for the Windows and macOS builds: neither platform
/// has a package manager that installs ImageMagick, and "install it separately" is the worst possible
/// answer there. Linux builds leave this out and use the distribution's own ImageMagick instead.
/// </summary>
public sealed class BundledImageMagick : IO.IImageMagick
{
    private BundledImageMagick() { }

    /// <summary>
    /// Loads the native library and returns the backend, or null when it cannot be loaded here. It is
    /// asked for its version rather than assumed to work, because a missing or mismatched native
    /// library only shows itself on the first call into it.
    /// </summary>
    public static BundledImageMagick? TryLoad()
    {
        try
        {
            _ = Magick.MagickNET.Version;
            return new BundledImageMagick();
        }
        catch (Exception error) when (error is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"The bundled ImageMagick could not be loaded: {error.Message}");
            return null;
        }
    }

    public byte[] Convert(string path, IO.ImageMagickOutput output)
    {
        try
        {
            // Only the first frame, exactly as the command line's "file[0]" does: a multi-page TIFF
            // would otherwise decode every page to keep one.
            using var image = new Magick.MagickImage(path, new Magick.MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
            image.AutoOrient();
            if (output == IO.ImageMagickOutput.Pam16) image.Depth = 16;
            return image.ToByteArray(output == IO.ImageMagickOutput.Png ? Magick.MagickFormat.Png : Magick.MagickFormat.Pam);
        }
        catch (Magick.MagickException error)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be decoded. {error.Message}".Trim(), error);
        }
    }
}
