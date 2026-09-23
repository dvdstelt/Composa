namespace Composa.Core.Tests;

/// <summary>
/// The in-app clipboard (<c>EditorSession.Clipboard</c> and <c>CopiedLayers</c>) is static, because it is shared
/// by every open project. xUnit runs test classes in parallel, so any class that copies, cuts or pastes joins this
/// collection: otherwise one class's copy lands between another's Copy and Paste, and the test fails only sometimes.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ClipboardCollection
{
    public const string Name = "Clipboard";
}
