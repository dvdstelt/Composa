namespace Composa.Editing;

/// <summary>What the View menu shows and snaps to. Carried from tab to tab like the tools; not saved with the project.</summary>
public sealed record ViewOptions
{
    /// <summary>Layout grid (View > Show > Grid). Off until turned on; independent of the 800% pixel grid.</summary>
    public bool ShowGrid { get; init; }
    /// <summary>User guides. Hidden extras do not snap.</summary>
    public bool ShowGuides { get; init; } = true;
    public bool ShowRulers { get; init; }
    /// <summary>Master snap switch (View > Snap).</summary>
    public bool Snap { get; init; } = true;
    public bool SnapToGuides { get; init; } = true;
    public bool SnapToGrid { get; init; }
    public bool SnapToLayers { get; init; } = true;
    public bool SnapToDocumentBounds { get; init; } = true;
    /// <summary>Locked guides cannot be dragged or created; Clear Guides still works.</summary>
    public bool LockGuides { get; init; }
}
