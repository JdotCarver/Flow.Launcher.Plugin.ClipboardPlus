// PreparedDuplicateDeletion.cs
namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Data.Models;

/// <summary>An opaque, transient proof that must be rechecked before deletion.</summary>
public sealed class PreparedDuplicateDeletion
{
    internal Record Keeper { get; }
    internal Record Duplicate { get; }

    internal PreparedDuplicateDeletion(Record keeper, Record duplicate)
    {
        Keeper = keeper;
        Duplicate = duplicate;
    }
}
