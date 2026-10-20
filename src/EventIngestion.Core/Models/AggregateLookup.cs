namespace EventIngestion.Core.Models;

public enum AggregateLookup
{
    Found,
    /// <summary>No window exists for the id (never seen, or swept after going idle).</summary>
    UnknownDevice,
    /// <summary>The device exists but every reading has aged out of the window.</summary>
    NoReadingsInWindow,
}
