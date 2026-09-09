# Connection Monitor

Connection Monitor is a Dalamud plugin for FINAL FANTASY XIV that measures endpoint ping, ICMP packet loss, RTT range, jitter, and manual TTL/ICMP route tests.

It supports saved monitoring-session history and experimental detection of the currently active public FFXIV endpoint.

## Features

- Manual endpoint target with persistent configuration
- Start/stop monitoring on demand
- Reset statistics without mixing results from different targets
- Packet loss, current RTT, average RTT, range, and jitter display
- Manual ICMP/TTL route test
- Saved history of up to 200 sessions
- Automatic reset when the monitored endpoint changes
- Experimental active game-endpoint detection

## Installation through Dalamud

The plugin becomes available after the first successful GitHub Actions release.

1. Start FFXIV and enter `/xlsettings`.
2. Open the **Experimental** tab.
3. Find **Custom Plugin Repositories**.
4. Add:

   ```text
   https://raw.githubusercontent.com/Kriss-Hietala/ConnectionMonitor/main/repo.json
   ```

5. Click **Save and Close**.
6. Enter `/xlplugins`.
7. Search for **Connection Monitor**.
8. Click **Install**.
9. Open it with `/connectionmonitor`.

## Endpoint auto-detection

Auto-detection reads established TCP connections and selects likely public FFXIV endpoints. It may identify a game-facing proxy or load balancer rather than the literal physical server hosting a duty, so treat it as experimental.

## Development build

Requirements:

- .NET 10 SDK
- Dalamud files required by `Dalamud.NET.Sdk/15.0.0`

Build locally:

```bash
~/.dotnet/dotnet build -c Release --no-logo
```

## Releases

Every push to `main` runs GitHub Actions. The workflow assigns version `1.0.<run-number>.0`, builds the plugin, creates `publish.zip`, creates a GitHub Release, and updates the manifests.

The release archive contains:

```text
ConnectionMonitor.dll
ConnectionMonitor.deps.json
ConnectionMonitor.json
```
