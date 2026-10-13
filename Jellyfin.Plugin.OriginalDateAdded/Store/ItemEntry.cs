using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

public sealed record ItemEntry(
    DateTime AddedAt,
    string Source,
    DateTime ScannedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Authoritative = false);
