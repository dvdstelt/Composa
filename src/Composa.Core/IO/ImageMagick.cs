using System.Diagnostics;

namespace Composa.IO;

/// <summary>What ImageMagick is asked to turn a file into.</summary>
public enum ImageMagickOutput
{
    /// <summary>An 8-bit PNG, which Skia then decodes like any other image.</summary>
    Png,
    /// <summary>A 16-bit PAM, which keeps a camera RAW decode's range for the develop step.</summary>
    Pam16,
}

/// <summary>One way of reaching ImageMagick: the installed command line, or the library bundled with the Windows and macOS builds.</summary>
public interface IImageMagick
{
    /// <summary>
    /// Converts the first frame of a file, turned upright by its EXIF orientation. Throws
    /// <see cref="InvalidDataException"/> with ImageMagick's own reason when the file cannot be read.
    /// </summary>
    byte[] Convert(string path, ImageMagickOutput output);

    /// <summary>
    /// Draws a vector file (an SVG) into a PNG over a transparent background, at <paramref name="scale"/> times the
    /// size it declares. Throws <see cref="InvalidDataException"/> with ImageMagick's own reason when it cannot.
    /// </summary>
    byte[] Rasterize(string path, double scale);
}

/// <summary>
/// The optional ImageMagick, which is how HEIC, AVIF, TIFF and camera RAW open: Skia decodes none of
/// them. Linux uses the system's command line, which every distribution ships; Windows and macOS have
/// no such thing to rely on, so their builds bundle the library and register it as <see cref="Bundled"/>.
/// Resolution happens once and is remembered, because it costs a process launch or a native library
/// load, and the answer does not change while the app runs.
/// </summary>
public static class ImageMagick
{
    private static readonly Lazy<IImageMagick?> resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Creates the bundled library, or returns null when it cannot be loaded here. The app sets this
    /// before anything is opened; it is a factory so the native library is loaded on first use rather
    /// than on the startup path.
    /// </summary>
    public static Func<IImageMagick?>? Bundled { get; set; }

    /// <summary>The ImageMagick in use, or null when there is none.</summary>
    public static IImageMagick? Current => resolved.Value;

    public static bool IsAvailable => Current != null;

    /// <summary>Converts through whichever ImageMagick is available, or returns null when there is none.</summary>
    public static byte[]? Convert(string path, ImageMagickOutput output) => Current?.Convert(path, output);

    // The system's ImageMagick comes first on Linux because that is what every packaged Linux build
    // uses, and a developer's build should behave the same way. Elsewhere the bundled library is the
    // one that was shipped and tested, so it wins over whatever happens to be installed.
    private static IImageMagick? Resolve() => OperatingSystem.IsLinux()
        ? ImageMagickTool.Find() ?? Bundled?.Invoke()
        : Bundled?.Invoke() ?? ImageMagickTool.Find();
}

/// <summary>ImageMagick's command line, run once per conversion with the result read from its standard output.</summary>
public sealed class ImageMagickTool : IImageMagick
{
    private ImageMagickTool(string executable) => Executable = executable;

    /// <summary>What is invoked, a bare name or a full path.</summary>
    public string Executable { get; }

    /// <summary>Finds an installed ImageMagick, or returns null when there is none.</summary>
    public static ImageMagickTool? Find() => Candidates().FirstOrDefault(IsImageMagick) is { } tool ? new ImageMagickTool(tool) : null;

    public byte[] Convert(string path, ImageMagickOutput output)
    {
        string[] arguments = output == ImageMagickOutput.Png
            ? [path + "[0]", "-auto-orient", "png:-"]
            : [path + "[0]", "-auto-orient", "-depth", "16", "pam:-"];
        // A RAW decode is several seconds of work on a large sensor, so it gets longer than an image.
        return Run(path, arguments, output == ImageMagickOutput.Png ? 60_000 : 180_000);
    }

    /// <summary>ImageMagick sizes an SVG from its resolution, 96 dots per inch being the size the file declares; the background must be set before the file is read.</summary>
    public byte[] Rasterize(string path, double scale) =>
        Run(path, ["-background", "none", "-density", (96 * scale).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), path + "[0]", "png:-"], 60_000);

    private byte[] Run(string path, string[] arguments, int timeout)
    {
        using var process = Start(arguments) ?? throw new InvalidDataException($"{Path.GetFileName(path)} could not be read: ImageMagick did not start.");
        using var result = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(result);
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new InvalidDataException($"{Path.GetFileName(path)} took too long to decode.");
        }
        if (process.ExitCode != 0 || result.Length == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be decoded. {FirstLine(errors.Result)}".Trim());
        return result.ToArray();
    }

    /// <summary>Starts ImageMagick with stdout and stderr redirected, or returns null when it cannot be started.</summary>
    public Process? Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try { return Process.Start(start); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    private static IEnumerable<string> Candidates()
    {
        yield return "magick"; // ImageMagick 7, and what every platform should find first.

        if (!OperatingSystem.IsWindows())
        {
            yield return "convert"; // ImageMagick 6, still shipped by several distributions.
            yield break;
        }

        // "convert" is deliberately never tried on Windows: C:\Windows\System32\convert.exe is the
        // FAT-to-NTFS volume converter and sits on the PATH of every Windows machine, so probing for
        // it finds the wrong program every time. ImageMagick 7 installs magick.exe in any case, and
        // its installer does not always put that on PATH, so the standard location is checked too.
        foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> installs;
            try { installs = Directory.EnumerateDirectories(root, "ImageMagick-*").OrderDescending(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var install in installs)
            {
                var candidate = Path.Combine(install, "magick.exe");
                if (File.Exists(candidate)) yield return candidate;
            }
        }
    }

    /// <summary>
    /// Asks a candidate to identify itself rather than trusting its name. Finding something called
    /// "convert" on PATH says nothing about what it actually is, on Windows or anywhere else.
    /// </summary>
    private static bool IsImageMagick(string tool)
    {
        try
        {
            var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start);
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return false;
            }
            return process.ExitCode == 0 && output.Result.Contains("ImageMagick", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false; // Not installed under this name, or not runnable.
        }
    }
}
