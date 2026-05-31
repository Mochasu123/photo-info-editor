namespace PhotoLocationEditor.App;

public sealed class DateCheckItem
{
    public Models.PhotoItem Photo { get; init; } = null!;
    public string? ExifDate { get; init; }        // A time
    public string FileDate { get; init; } = "";    // B time = min(creation, modification)
    public DateTime FileCreation { get; init; }
    public DateTime FileModification { get; init; }
    public string Category { get; init; } = "";    // "A"/"B"/"C"/"D"
    public string Detail { get; init; } = "";
}
