namespace Composa.Core.Tests;

/// <summary>
/// Removes the temporary files a test wrote. On Windows a file that was only just written and read is often still open
/// in another process for a moment (the virus scanner looks at every new file), and a delete then fails with a sharing
/// violation. Cleanup is not what a test is about, so this retries briefly and then leaves the file to the temp folder
/// rather than failing the test, or hiding the assertion that failed before it.
/// </summary>
internal static class TempFiles
{
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20) return;
                Thread.Sleep(50);
            }
        }
    }
}
