using System.Diagnostics;

namespace Composa.IO;

/// <summary>
/// Finds the optional ImageMagick command line, which is how HEIC, AVIF, TIFF and camera RAW open:
/// Skia decodes none of them. Resolution happens once and is remembered, because it costs a process
/// launch and the answer does not change while the app runs.
/// </summary>
public static class ImageMagick
{
    private static readonly Lazy<string?> resolved = new(Find, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>What to invoke, a bare name or a full path, or null when ImageMagick is not installed.</summary>
    public static string? Executable => resolved.Value;

    public static bool IsAvailable => Executable != null;

    /// <summary>Starts ImageMagick with stdout and stderr redirected, or returns null when it is not installed.</summary>
    public static Process? Start(params string[] arguments)
    {
        if (Executable is not { } tool) return null;
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try { return Process.Start(start); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    private static string? Find() => Candidates().FirstOrDefault(IsImageMagick);

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
