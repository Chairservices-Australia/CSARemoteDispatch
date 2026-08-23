# CSA Remote Dispatch

A fork of [mspielberg/dv-remote-dispatch](https://github.com/mspielberg/dv-remote-dispatch)
(Remote Dispatch by Zeibach) for Derail Valley, renamed so it installs and shows
up in Unity Mod Manager separately from the upstream mod.

Forked at upstream `v1.2.1` (`0e032da`).

## Changes from upstream

- **Larger car/loco icons** — a Car size setting (Normal/Large/Larger/Huge) scales
  the map footprint of every car so consists stay visible when zoomed out.
- **Station labels** — station name, yard ID and the station's real in-game colour
  drawn on the map, sourced live from the game's own `StationInfo` (so the palette
  stays correct across game updates rather than being hardcoded). Toggleable.
- Renamed to `CSARemoteDispatch` so it coexists with the upstream mod.

## Building

The project references assemblies from the game install. Those paths are
machine-specific, so they live in `Directory.Build.targets`, which is gitignored:

```
cp Directory.Build.targets.example Directory.Build.targets
# edit DvInstallDir to point at your Derail Valley install
dotnet build
```

Build and install into the game in one step:

```
dotnet build -p:DeployToGame=true
```

This clears Unity Mod Manager's stale `*.cache` file so the new build is actually
loaded. A plain `dotnet build` never touches the game install.

> **Only enable one of CSA Remote Dispatch and the upstream Remote Dispatch at a
> time.** Both apply the same Harmony patches and both bind the same HTTP port,
> so running them together will conflict.

##### Credits

Original mod by Miles Spielberg (Zeibach) — see [LICENSE](LICENSE).

Icons made by [Freepik](https://www.freepik.com) from [Flaticon](https://www.flaticon.com/).
