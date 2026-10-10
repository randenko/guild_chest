# Guild Chest

A Valheim 1.x mod that makes every guild chest in a world an access point to
one shared inventory. Supports single-player, player-hosted multiplayer, and
dedicated servers. The mod and its dependencies must be installed on every
client and the host, with matching Guild Chest versions.

## Gameplay

- Build a **Guild Chest** with the hammer near a workbench, using **20 fine wood,
  10 iron, 2 surtling cores, and 250 gold**. It resembles a blue-tinted reinforced
  chest.
- Every guild chest accesses the same **32 slots (8 × 4)**, even across the
  entire map or when other chest locations are unloaded.
- One player can use the shared inventory at a time. Normal ward permissions
  apply at the chest being opened.
- Drag, split, quick-transfer, Take All, and Stack All work through the normal
  inventory window. Transfers wait for the host to acknowledge them.
- Metals are accepted. Item quality, durability, crafter data, and custom data
  use Valheim's serialization. Stack merging follows vanilla rules.
- Move an item into your player inventory before using it or dropping it on
  the ground. Additional guild chests add access points, not capacity.
- Destroying a chest refunds its construction materials normally. If another
  guild chest exists anywhere in the world, contents remain shared. Destroying
  the **last** chest drops the shared contents at its location and clears storage.
- Each world has its own inventory. Shared contents are stored in the world
  save, rather than a player character or a separate database.

## Develop in VS Code

Host prerequisites: **VS Code**, its **Dev Containers** extension, and a running
Docker Engine / Docker Desktop configured for Linux containers. The SteamCMD
workflow uses x86-64 Linux; this repository's container was built on an x86-64
Linux host. No host .NET SDK or Unity Editor is required.

1. Open this repository in VS Code.
2. Run **Dev Containers: Reopen in Container** from the Command Palette.
3. Wait for the container's automatic setup to finish. It downloads roughly
   2.2 GB of Valheim Dedicated Server files using anonymous SteamCMD login,
   fetches pinned BepInEx and Jötunn dependencies, and then restores NuGet
   packages. No Steam account credentials are needed. Existing references are
   reused when rebuilding the container.

4. Run **Build Debug** (`Ctrl+Shift+B`) and **Run Tests**, or:

   ```bash
   bash scripts/dev.sh build
   bash scripts/dev.sh test
   ```

5. Run **Package Release**, or:

   ```bash
   bash scripts/dev.sh package
   ```

The release ZIP is written to `artifacts/GuildChest-1.0.9.zip`, visible in the
host workspace. Container rebuilds retain downloaded references in `.local`
and NuGet packages in the `guild-chest-nuget` Docker volume. Post-create downloads
missing references and restores NuGet packages; updating existing game references
still requires the explicit `setup --update` command below.

The C# extension is configured to handle the SDK-style `net48` plugin. If
VS Code opens before automatic setup finishes, wait for it to complete and then
run **Developer: Reload Window** to refresh unresolved game symbols. If reference
setup fails, retry with **Tasks: Run Task → Setup References** or
`bash scripts/dev.sh setup`.

To debug automated tests, start **Debug Test Host**, wait for its process ID,
then select **Attach to .NET test host** in the Run and Debug panel and pick that
`testhost` process. Gameplay runs in your separately installed Valheim client
or server; inspect its `BepInEx/LogOutput.log` for mod diagnostics.

## References and builds

The plugin targets **.NET Framework 4.8**, its game-independent core targets
**.NET Standard 2.0**, and tests run on **.NET 10**. Linux builds obtain the
Framework reference assemblies from NuGet. The dev container image is pinned
by digest, and NuGet lock files are checked in.

`ValheimManagedDir` defaults to the downloaded server's
`.local/valheim-server/valheim_server_Data/Managed` directory. Override it when
building against a different client or server installation:

```bash
bash scripts/dev.sh build -p:ValheimManagedDir=/path/to/Managed
```

Game downloads and dependency DLLs are ignored by Git and excluded from the
release ZIP. `.local/references.json` records the Steam manifest/build ID,
dependency versions, and assembly SHA-256 hashes. To update the game references
deliberately and refetch pinned mod dependencies:

```bash
bash scripts/dev.sh setup --update
```

Normal builds never invoke SteamCMD. The original game assemblies are referenced
directly; Harmony accesses the few private fields and methods needed by the mod.
No publicizer or proprietary assemblies are committed.

See [code structure](docs/ARCHITECTURE.md) for module responsibilities and adapter
boundaries. Change `version_number` in `manifest.json` to set the release version;
the build generates the plugin constant and assembly versions. Packaging checks
the build receipt against both DLLs and rejects stale or replaced binaries.

## Install and test in Valheim

Install **BepInExPack Valheim 5.4.2351** and **Jötunn 2.30.2** using a compatible
mod manager or their manual installation instructions. Extract the release ZIP's
`BepInEx` directory into each modded client and server installation. Both
`GuildChest.dll` and `GuildChest.Core.dll` belong together under
`BepInEx/plugins/GuildChest`.

Restart the game/server after installing or replacing DLLs. For a dedicated
server, stop it before replacing binaries. Build and test with a disposable
world before adding the mod to a world you intend to keep.

This is a mod for PCs with BepInEx installed; console clients cannot load the
custom chest. Version 1.0.2 adds optional adapters for AzuAutoStore and
AzuCraftyBoxes. See [storage-mod compatibility](docs/COMPATIBILITY.md) for the
supported operations, range configuration, and integration API for other mods.

Back up the complete world before removing the mod: the shared state and chest
prefabs require Guild Chest to be installed when loading/saving that world.
Save restoration still follows Valheim's separate world/character save behavior;
this mod does not provide atomic saves across both or protection against edited
characters and malicious clients.

See [the gameplay verification checklist](docs/VALIDATION.md) for the exact
build tested and the scenarios that require running Valheim.

### Transfer rejection troubleshooting

Version 1.0.1 fixes transfers being blocked by an unrelated item in the player's
inventory whose prefab is absent on the server. Such items stay in the player's
inventory with their metadata intact. Items deposited into shared storage still
require their item mod on both the server and every client that accesses them.

A rejected transfer now displays the server's reason and leaves the player's
items untouched. Check `BepInEx/LogOutput.log` for the inventory being read and
the missing prefab hash, invalid stack, expired session, or access failure.
Update both Guild Chest DLLs on the server and clients together; restart them
after replacement. Existing world inventories retain their save format.

## Project structure

- `src/GuildChest`: prefab, vanilla UI adapter, RPC protocol, native serialization,
  persistent world state, ward checks, and Harmony patches.
- `src/GuildChest.Core`: world inventory revisions, leases, chest membership,
  retry receipts, and final-chest removal rules.
- `tests/GuildChest.Tests`: game-independent synchronization tests.
- `tests/GuildChest.NativeSmoke`: optional native fixtures for a disposable server;
  see the validation notes. This test plugin is excluded from the release ZIP.
- `scripts`: reference setup, build/test entrypoint, and deterministic packaging.
