using System.Diagnostics;
using System.Text.Json;
using Composa.Editing;
using Composa.IO;
using Composa.Model;

namespace Composa.App;

/// <summary>
/// Crash protection: modified documents are written to the cache directory every few minutes, from a snapshot and
/// off the UI thread (committed bitmaps are immutable, so a snapshot can be read safely while editing continues).
/// A clean close removes them; whatever is still there at the next launch is offered for recovery.
/// </summary>
public sealed class Recovery
{
    public sealed record Entry(string ProjectPath, string InfoPath, string Title, string? OriginalPath, DateTime SavedAt);

    private sealed record Info(string Title, string? OriginalPath, DateTime SavedAt, int ProcessId);

    private readonly string directory;
    private readonly Dictionary<EditorSession, (Guid Id, int Revision)> saved = [];
    private Task running = Task.CompletedTask;

    public Recovery(string? directory = null) => this.directory = directory ?? Path.Combine(AppPaths.Cache, "recovery");

    /// <summary>Writes every modified session that changed since its last autosave. Returns the task doing the writing.</summary>
    public Task Save(IEnumerable<EditorSession> sessions)
    {
        if (!running.IsCompleted) return running; // A large document may still be encoding; try again next time.
        var work = new List<(Document Snapshot, string Path, Info Info)>();
        foreach (var session in sessions)
        {
            if (!session.IsModified || session.IsInteracting) continue;
            var known = saved.TryGetValue(session, out var state);
            if (known && state.Revision == session.Revision) continue;
            var id = known ? state.Id : Guid.NewGuid();
            saved[session] = (id, session.Revision);
            work.Add((session.Document.Clone(), Path.Combine(directory, id + ProjectFile.Extension),
                new Info(session.Title, session.FilePath, DateTime.Now, Environment.ProcessId)));
        }
        if (work.Count == 0) return Task.CompletedTask;
        return running = Task.Run(() =>
        {
            Directory.CreateDirectory(directory);
            foreach (var (snapshot, path, info) in work)
            {
                try
                {
                    ProjectFile.Save(snapshot, path);
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(info));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Recovery copies are best effort: a full or read-only cache must not interrupt editing.
                    Console.Error.WriteLine($"Autosave failed: {error.Message}");
                }
            }
        });
    }

    /// <summary>Forgets a session that was saved or closed on purpose, deleting its recovery copy.</summary>
    public void Forget(EditorSession session)
    {
        if (!saved.Remove(session, out var state)) return;
        var path = Path.Combine(directory, state.Id + ProjectFile.Extension);
        running = running.ContinueWith(_ => Delete(path));
    }

    /// <summary>Recovery copies left behind by an instance that is no longer running.</summary>
    public List<Entry> FindAbandoned()
    {
        var found = new List<Entry>();
        if (!Directory.Exists(directory)) return found;
        foreach (var infoPath in Directory.GetFiles(directory, "*.json"))
        {
            var projectPath = Path.ChangeExtension(infoPath, ProjectFile.Extension);
            try
            {
                var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(infoPath));
                if (info == null || !File.Exists(projectPath) || IsAlive(info.ProcessId)) continue;
                found.Add(new Entry(projectPath, infoPath, info.Title, info.OriginalPath, info.SavedAt));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { /* An unreadable entry cannot be recovered; leave it alone. */ }
        }
        return found.OrderBy(e => e.SavedAt).ToList();
    }

    public void Discard(Entry entry) => Delete(entry.ProjectPath);

    private static void Delete(string projectPath)
    {
        try
        {
            File.Delete(projectPath);
            File.Delete(Path.ChangeExtension(projectPath, ".json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* It will be offered again next launch. */ }
    }

    private static bool IsAlive(int processId)
    {
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.ProcessName.Contains("composa", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}
