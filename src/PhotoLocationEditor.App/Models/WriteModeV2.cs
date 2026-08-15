namespace PhotoLocationEditor.App.Models;

/// <summary>
/// Backup mode has been removed. Writing is either done on copies in a new
/// output directory, or directly on the original files.
/// </summary>
public enum WriteMode
{
    CopyToOutputDirectory = 0,
    DirectInPlace = 1
}
