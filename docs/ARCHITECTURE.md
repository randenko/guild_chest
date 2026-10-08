# Code structure

Guild Chest runs on Unity's main thread. The host owns the shared inventory;
clients stage transfers and apply them after acknowledgement. Optional storage
mods supply their own rules through adapters.

| Module | Responsibility |
| --- | --- |
| `GuildChest.Core` | Leases, revisions, replay receipts and transfer/completion state |
| `Protocol` | V1 operation numbers, package headers, dimensions and timing constants |
| `Host` | World persistence, access checks, conservation and final-chest spilling |
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

Required adapter members are resolved before any hooks are installed. Failed
installation removes that adapter's hooks and logs its unavailable bindings.
Inventory guards remain active. Wrapper lookups are cached by type; configuration
values are read when used, so upstream setting changes still apply.

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

`build/ValheimReferences.props` holds the shared game/loader references and
Framework reference-assembly package for the plugin and native fixture.
Game-independent projects do not import it.

Run `bash scripts/dev.sh test` for core lifecycle and package-consistency tests.
The optional native fixture exercises real game and QoL assemblies, including
scope restoration, adapter installation, deposits, resource previews and portal
placement. See [validation](VALIDATION.md) for how to run it and its limits.
