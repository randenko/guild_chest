# Validation

Reference game version: **Valheim 1.0.16**, Steam build **25527701**, obtained through anonymous SteamCMD
app 896660 on 2026-10-05. Exact Steam build ID and reference hashes are recorded
locally in `.local/references.json`. A successful compile checks compatibility
with this build's assemblies; future 1.x updates need verification.

## Completed checks (2026-10-05)

- The dev container image builds successfully and runs the solution as the
  non-root `vscode` user with .NET SDK 10.0.401.
- Full solution and Release plugin builds succeed without warnings or errors.
- All **8** core synchronization tests pass.
- The reference setup succeeds through anonymous SteamCMD; a repeated setup
  skips installed downloads. Package metadata and the 256 × 256 PNG are valid,
  and the release ZIP contains only the intended seven files.
- A native headless Valheim 1.0.16 fixture verifies prefab registration, forge
  recipe, dimensions, serialization of 12 iron and a damaged quality-3 iron sword
  with crafter/custom data, and saving the inventory with two distant chests.
- Restarting that fixture restores both unloaded chest records and their shared
  inventory. Removing the first preserves contents; removing the last emits
  exactly two item stacks, preserves the sword metadata, and clears the store.
- A second native fixture drives a synthetic host player through the real request
  and response RPCs and vanilla inventory handlers. Opening, quick deposit, Take
  All, partial-stack drag deposit/withdrawal, Stack All, and releasing the lease
  pass without changing the real player inventory before acknowledgement.
- Version **1.0.1** repeats that transport fixture with an unrelated client-only
  item whose prefab is absent from the server's ObjectDB. All transfers pass,
  retaining that item and its quality/custom data in the player's inventory.
  Additional native regression checks accept local rearrangement, reject changes
  to the unresolved item's metadata or quantity, reject duplicate inventory
  slots, and still reject an unresolved prefab in shared storage. All 8 core
  tests and the Release build pass for this version.

The scripted host fixture disables dedicated-scene culling, structural wear on
its floating chest, and the headless scene's GUI Update loop. It binds the native
inventory grids explicitly. This validates handlers and RPCs, not rendering or
the normal player/input lifecycle.

Visual appearance, live controller input, and remote multiplayer clients remain
manual checks below; headless testing does not establish those results.

## Reusing the native fixture

The optional `tests/GuildChest.NativeSmoke` project is a BepInEx test plugin,
excluded from the release package and normal test command. Build it explicitly:

```bash
dotnet build tests/GuildChest.NativeSmoke/GuildChest.NativeSmoke.csproj -c Release
```

Install its DLL alongside Guild Chest in an isolated Valheim 1.x dedicated-server
installation. It runs only with `GUILDCHEST_SMOKE=1` and a world name beginning
with `GuildChestNative`. Set `GUILDCHEST_SMOKE_MARKER` to an absolute writable
path. The first run with that marker absent creates two distant chests and saves
sample contents; the second run of that world verifies persistence and final
destruction. It exits the server when finished and writes the marker or a
`.failed` file.

For the scripted host-transfer checks, use another fresh disposable world and
marker, and also set `GUILDCHEST_SMOKE_TRANSPORT=1`. Never install this test
plugin in a normal game or world: it creates/deletes fixture objects and exits
the process. A Linux headless server additionally needs its native runtime
libraries, including PulseAudio libraries; these are not part of the development-only
dev container. Run BepInEx with the normal server loader for your platform.

## Automated and container checks

- Build the actual dev container Dockerfile.
- Restore and compile the full solution inside the container.
- Run the synchronization tests: global exclusivity, lease expiry/renewal,
  unauthorized mutations, stale revisions, retry ordering/idempotency, duplicate
  registrations/removals, unloaded-chest membership, final-chest spill, and
  separate/restored world stores.
- Package Release and inspect its ZIP for both plugin DLLs, README, icon,
  license, and manifest. Game and dependency DLLs must be absent.

## Gameplay checklist

Record mode, game build, dependencies, result, and relevant logs for each run.
These checks require a running client; automated core tests cannot verify UI,
rendering, controller input, or a multiplayer player's inventory.

- [ ] Solo: guild chest appears in the hammer menu; recipe needs a forge and
  the correct resources; the model is distinctly blue; vanilla chest materials
  are unchanged.
- [ ] Deposit at A, travel far enough to unload A, withdraw at B. Repeat across
  distant islands and with more than two chests.
- [ ] Drag, quick-transfer, split-stack, swap occupied slots, Take All, Stack All,
  controller input, full shared storage, and a nearly full player inventory.
- [ ] Transfer metals, damaged/upgraded weapons, crafted items, and custom-data
  items; compare item metadata after transfer and save/restart.
- [ ] Two players open different chests together: exactly one gets access.
  Close, die, or disconnect; the other player can then acquire access.
- [ ] Artificial latency: retry a transfer; it changes both inventories once.
  Close the UI, walk away, or destroy the chest while acknowledgement is pending.
- [ ] Ward owner/permitted player can open a protected chest; another player
  cannot. Test overlapping wards and a dedicated server with unloaded wards.
- [ ] Destroy a chest while another is unloaded: no shared-content drops.
  Destroy the final chest: resources and shared contents drop once, with metadata.
  Rebuild a chest: the shared inventory is empty.
- [ ] Save/restart with distant chests and contents; repeat in a second world
  and with the same character to verify world isolation.
- [ ] Missing/mismatched Guild Chest plugin prevents multiplayer entry.
- [ ] Repeat in player-hosted multiplayer and on a dedicated server with two
  modded clients. Verify ordinary chests still behave normally.

## Recovery behavior

If final-chest spilling fails, the remaining items and progress stay in the world
state. The host retries on world load; already recorded drops are not emitted
again. Missing item prefabs or unsupported serialization block access while
preserving stored bytes. Restore the appropriate item mod/version before retrying.
Unresolved prefabs in the player's inventory can remain there during unrelated
transfers. They cannot enter shared storage until the server has their prefab.
Transfer failures now return the server's reason to the client and log the
request operation, sender, chest, and sequence without logging the lease token.

Per-transfer acknowledgement receipts are retained in memory for the last 256
accepted operations. They resolve ordinary delayed/repeated messages, including
after closing or destruction, but are not a cross-restart transaction journal.
