using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public class RecoveryTests
{
    [Fact]
    public async Task Modified_documents_are_autosaved_and_forgotten_once_saved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recovery = new Recovery(folder);
            var session = EditorSession.NewCanvas(40, 30, SKColors.White);
            await recovery.Save([session]);
            Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0); // Nothing changed yet.

            session.Fill(SKColors.Red);
            await recovery.Save([session]);
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            var written = File.GetLastWriteTimeUtc(Directory.GetFiles(folder, "*.cmps")[0]);
            await recovery.Save([session]);
            Assert.Equal(written, File.GetLastWriteTimeUtc(Directory.GetFiles(folder, "*.cmps")[0])); // Unchanged since: not rewritten.

            // This process is alive, so its own copies are not "abandoned".
            Assert.Empty(recovery.FindAbandoned());

            // A copy left by a process that no longer exists is.
            var info = Directory.GetFiles(folder, "*.json")[0];
            File.WriteAllText(info, File.ReadAllText(info).Replace($"\"ProcessId\":{Environment.ProcessId}", "\"ProcessId\":2147483600"));
            var abandoned = Assert.Single(new Recovery(folder).FindAbandoned());
            Assert.Equal("Untitled", abandoned.Title);
            var restored = Composa.IO.ProjectFile.Load(abandoned.ProjectPath);
            Assert.Equal(SKColors.Red, restored.Layers[0].Pixels!.GetPixel(3, 3));

            recovery.Forget(session);
            await recovery.Save([]);
            await Task.Delay(100);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
