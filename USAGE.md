# HostWitness Usage (Beta)

## Startup and UI

- **Run**: Execute `HostWitness.exe`. For VSS / Offline Hive / Dump, run as **Administrator**.
- **On startup**: The default tab is **System Info**. The top row is dynamic tabs (Timeline / Live Stream / Live Process / Live TCP View); the row below is static tabs (System Info, Process View, Recent Files, Prefetch, Amcache, Autorun, Event Log, Netstat, Browsing History).
- **Menu**: File (Export Snapshot, Exit), Advanced (Drill Down, Settings), Help (About).

## Common operations

- **Timeline**: Shows the event timeline. From Live Process, select a process and right-click **Show related events (Drill down)** to jump to Timeline filtered by that process; use **Clear process filter** to show all events again.
- **Live Process**: Filter Bar, Columns management, right-click Apply as Filter / Copy Value / Open Directory, **Create Dump > Create minidump / Create full dump**; **Show process tree** toggles tree view.
- **Live TCP View**: Live connection list; toolbar Resolve, States filter, TCP/UDP v4/v6 toggles.
- **Detach tab**: Right-click a dynamic tab → **Detach to Window** to open it in a separate window; use **Restore** in the main or floating toolbar to bring it back.
- **Settings**: Advanced → Settings to adjust PID cache, font size, Activity Index Max events, time zone display (Local/UTC); stored in `%AppData%\HostWitness\settings.json`.

## Advanced and limitations

- **VSS / Offline Hive**: Requires Administrator and Volume Shadow Copy service; falls back to live paths on failure.
- **Startup errors**: If the app does not start, check `%AppData%\HostWitness\logs\startup.log`.
- For more limitations and risks, see the project documentation (if linked).
