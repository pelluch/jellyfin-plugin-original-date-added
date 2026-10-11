using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public sealed record SourceItem(BaseItem Item, DateTime Original);
