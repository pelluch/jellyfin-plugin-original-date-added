# Original Date Added

A Jellyfin plugin that keeps an item's original date added when its file is replaced.

When a file in Jellyfin is replaced, and its path changes, the item's Jellyfin ID, which is based on paths, also changes. Jellyfin is able to link the data associated to the replaced item with the new one, but the item will get placed in "Recently Added".

This behavior may be desirable for some, but in certain flows, the video doesn't materially change (for instance, Tdarr transcodes, and causes a rename to reflect new properties), and having it be put in Recently Added adds noise and makes users think it's an entirely new episode / movie / show.

This plugin does two things:

1. Builds a local "database" (as a JSON file) of items, alongside their currently best-known "Added" date.
2. Connects with other providers, such as Tracearr and Jellystat, to attempt to get from them a best known "earliest added" date
3. Enables a scheduled task (no schedules by default) to perform the above tasks
4. Acts as a metadata provider that can run on file import, so this can happen as soon as a video is available.

## Build

```sh
dotnet build -c Release
```

Requires the .NET 10 SDK. Targets Jellyfin 12.0 and later.
