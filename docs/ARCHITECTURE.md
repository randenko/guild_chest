# Code structure

Guild Chest runs on Unity's main thread. The host owns the shared inventory;
clients stage transfers and apply them after acknowledgement. Optional storage
mods supply their own rules through adapters.

| Module | Responsibility |
| --- | --- |
| `Plugin` | Startup, prefab registration, hammer icon and piece ordering |
| `ChestAppearance` | Gold chest tint and mounted coin geometry |
| `GuildChest.Core` | Leases, revisions, replay receipts and transfer/completion state |
| `Protocol` | V1 operation numbers, package headers, dimensions and timing constants |
| `Host` | World persistence, access checks, conservation and access-point removal |
| `Client` | Sessions, staged transfers, acknowledgement and local application |
| `GuildChestStorage` | Public transaction API for other mods |
| `InventoryAccess` | Protected inventories and disposable upstream inventory scopes |
| `StorageScopes` | Balanced UI, preview and consumption scopes for Harmony patches |
| `StoragePreview` | Read-only polling, freshness and revision ordering |
| `AutoStoreAdapter` / `AutoStoreBatch` | Upstream deposit rules / ordered asynchronous destinations |
| `CraftyBoxesAdapter` | Upstream counts, caches, range and pull permissions |
| `MaterialPlanner` / `MaterialSupply` | Resource deficits / acknowledged material transfer |
| `BuildingContinuation` | Resume the native placement loop after material acknowledgement |
| `StorageBindings` / `ContainerBindings` | Validated optional-mod bindings / cached wrapper access |
| `StorageCompatibility` | Adapter installation, updates and world reset |

The guild chest's hammer icon is rendered from its prefab after
`ChestAppearance.Apply` adds the gold tint and coin decorations. Using Jötunn's
isometric Euler angles `(x, y, z)` as the base, the final icon rotation is
`(x - 6, -y - 5, -z + 7.5)` degrees. These angles apply to the icon's render
clone. The facing direction and viewing angle were tuned against the vanilla
chest icons in the client hammer menu.

`Plugin` handles `PieceManager.OnPiecesRegistered` by moving the guild chest
immediately after the reinforced chest (`piece_chest`) in the hammer's piece
list. The handler remains subscribed across world loads so rebuilt piece tables
receive the same ordering, and is unsubscribed on plugin shutdown. Missing
hammer or chest entries leave the current list intact.

Required adapter members are resolved before any hooks are installed. Failed
installation removes that adapter's hooks and logs its unavailable bindings.
Inventory guards remain active. Wrapper lookups are cached by type; configuration
values are read when used, so upstream setting changes still apply.

Chest removal revokes the chest's lease and always retains the inventory and
revision in the independent world-state record, including with zero access
points. Registering a rebuilt chest restores access after removal or a world
reload. New removals never create spill journals. Pending spills saved by older
versions continue their existing recovery process on world load.

Upstream transfer rules run inside `InventoryAccess.StagingScope`, which restores
the original player and chest inventories on disposal. It also restores an
enclosing scope. Transfer actions must not retain disposable inventory references
or perform gameplay effects. Effects and continuations follow acknowledgement.
Preview counts cannot fund a consumer outside the supported crafting/building
paths.

`manifest.json` is the release-version source. MSBuild reads its `version_number`
for assembly versions and generates the constant used by the BepInEx plugin
attribute. A build receipt records the version and both DLL hashes. Packaging
rejects a stale version or a replaced DLL before creating the archive; the receipt
is a consistency check and is excluded from releases.

ZIP entry timestamps are derived from each file's SHA-256 within the ZIP date
range and its two-second precision. Identical inputs produce identical archives
regardless of filesystem modification times or the packaging machine's timezone.
Changed DLLs receive different timestamps even when their sizes match. This lets
Docker's rsync copy updated binaries and makes BepInEx refresh cached plugin
versions. These dates identify file contents; they do not record build times.

`build/ValheimReferences.props` holds the shared game/loader references and
Framework reference-assembly package for the plugin and native fixture.
Game-independent projects do not import it.

Run `dotnet run --project build -- --target Test` for core lifecycle,
package-consistency, and build orchestration tests. The standalone NUKE project
in `build/` coordinates the existing Python helpers and MSBuild targets; it is
excluded from the product solution and release ZIP.
The optional native fixture exercises real game and QoL assemblies, including
scope restoration, adapter installation, deposits, resource previews and portal
placement. See [validation](VALIDATION.md) for how to run it and its limits.
