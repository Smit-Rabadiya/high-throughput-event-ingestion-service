namespace EventIngestion.Core.Models;

/// <summary>One sensor reading. Timestamp is unix milliseconds (UTC): 8 bytes, cheap to compare.</summary>
public readonly record struct Reading(string DeviceId, long TimestampUnixMs, double Value);
