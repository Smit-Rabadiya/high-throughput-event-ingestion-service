namespace EventIngestion.Core.Models;

/// <summary>Consistent point-in-time aggregates for one device window.</summary>
public readonly record struct AggregateSnapshot(long Count, double Min, double Max, double Average);
