# Validation

Recorded native/gameplay reference: **Valheim 1.0.16**, Steam build **25527701**,
obtained through anonymous SteamCMD app 896660 on 2026-10-05. Current setup
provenance is recorded locally in `.local/references.json`; the publication record
below identifies the newer release build. A successful compile checks assembly
compatibility; runtime behavior on newer builds needs separate verification.

## Steam reference update recovery (2026-10-15)

- Devcontainer startup failed updating Steam build 25730807 to 25909431:
  Steam fetched the current public depot manifest but denied access to the
  previously installed manifest while comparing the cached installation.
- Setup now retries a failed cached install once with fresh installation
  metadata, preserving server files and a backup of the previous app manifest.
  Failed recovery restores the metadata and stops before recording new reference
  provenance. Regression tests cover successive game releases, recovery failure,
  missing replacement metadata, cold install failure, and app metadata failure.
- The exact startup command, `dotnet run --project build -- --target Restore
  --update-references`, reproduced the failure and recovered automatically in
  the existing installation. Setup and restore succeeded against build 25909431.
  The standalone setup fix passes 15 C# and 68 Python tests; Compile succeeds with
  no warnings or errors against the refreshed references. Gameplay on this newer
  build remains unvalidated.

## 1.1.0 publication (2026-10-13)

- Release preparation merged through PR #21 after both CI checks passed. Tag
  v1.1.0 points to 586488e2a61af23ded27d7943798f43c294ab836.
- The GitHub tag workflow failed three times while SteamCMD reported missing
  configuration. A clean download with the public branch selected explicitly
  succeeded, and the complete local NUKE Release target then passed against
  fresh Steam build 25730807 with 15 C# and 62 Python tests and no compiler
  warnings or errors. Previous references were preserved locally. This records
  compilation against the newer build; it does not claim new live or native
  fixture checks for that build.
- GitHub and Thunderstore both publish version 1.1.0. The downloadable ZIPs are
  identical, with SHA-256
  `133b8fe196e45c49fc0d984d07b9f948a070ca25557fe4b7eab8baa70bf7dbf3`.
- The Thunderstore upload succeeded, verifying token authorization. Its first
  verification step saw a CDN-cached pre-publication 404. Metadata checks now use
  fresh query keys so a retry can detect the existing archive and skip uploading.
- The follow-up checks pass 15 C# and 63 Python tests, including cached-404
  regression coverage. A cold CI run still failed with explicit public-branch
  selection alone. Reference setup now refreshes app metadata in a separate
  session before installing the public branch, following the successful local
  recovery sequence.
- Official tcli 0.2.4 installed the published package into a new isolated
  `GuildChest-1.1.0-launch` profile, resolving BepInExPack Valheim 5.4.2351 and
  Jötunn 2.30.2 automatically. Both installed Guild Chest DLLs match the release
  archive bytes and have assembly/file version 1.1.0.0. This is a profile
  installation check; no gameplay was launched from that profile.
- The live listing renders the player README, all four screenshots with alt text,
  recipe, configuration, Ko-fi support, and video links. All four release-specific
  image downloads match the reviewed media. The user confirms applying the
  updated YouTube description, including the verified listing URL.

## 1.1.0 launch preparation (2026-10-13)

- The normal NUKE Test target passes 15 C# tests and 62 Python tests, including
  package listing/changelog, release links, icon/manifest validation, receipt
  and DLL-update regressions, reproducibility, and mocked publisher scenarios.
- Release/package/checksum builds succeed without compiler warnings or errors
  using the existing downloaded references. Both DLLs have assembly and file
  version 1.1.0.0; the generated plugin version, manifest, notes, and ZIP use 1.1.0.
- The package passes ZIP integrity, allowlist, icon, version and SHA-256 checks.
  The changelog comes from the current notes, document and media links are pinned
  to v1.1.0, and screenshots remain outside the ZIP. Repackaging identical inputs
  produces identical archive bytes.
- actionlint 1.7.12 accepts the release and Thunderstore workflows. The official
  tcli 0.2.4 binary parses the generated TOML in a separate local build check;
  this check does not publish or replace the installable Guild Chest archive.
- Read-only Thunderstore API checks confirm both pinned dependency versions and
  the three selected Valheim categories. Publisher tests cover unpublished and
  invalid releases, ancestry/version/checksum conflicts, absent configuration,
  dependencies/categories, matching/conflicting existing versions, and retries.
  Preflight never invokes the upload CLI. Upload authorization remains untested.
- Presentation images were inspected for the complete chest grid and readable
  20/10/2/250 recipe costs; all four staged originals retain their original bytes.
  A GitHub-rendered Markdown preview with embedded images is generated locally
  under `.local/launch-review/`. The saved YouTube description matches the review
  plan; the user confirms applying it to YouTube.
- Repository, issues, latest release, dependency pages, and video links return
  HTTP 200. Ko-fi returns HTTP 403 to automated requests; the user has verified
  the link manually.
  New release-specific URLs become available when v1.1.0 is committed and tagged.
- The user confirms live gameplay verification for launch. This confirmation
  does not include individual checklist results, game modes, build/dependency
  versions, or logs; the detailed scenarios below remain available as a reference.

Gameplay, Ko-fi, and the updated YouTube description are user-confirmed. Clean
mod-manager installation, dependency resolution, both DLL versions, listing
rendering, and the downloadable ZIP checksum were verified after publication,
as recorded above. The verified Thunderstore listing URL is included in both
the saved and live YouTube descriptions. The 1.1.0 launch checks are complete.

## Completed checks (through 2026-10-12)

- The dev container image builds successfully and runs the solution as the
  non-root `vscode` user with .NET SDK 10.0.401.
- Full solution and Release plugin builds succeed without warnings or errors.
- All **15** core synchronization/lifecycle tests and **38** Python packaging,
  release-validation and build-orchestration tests pass.
- The hammer-menu icon is rendered from the gold-decorated chest prefab, with
  its facing direction and viewing angle tuned to match the vanilla chest icons.
  The user visually confirmed the final icon, including all three angle
  adjustments, in the client hammer menu.
- Native inspection confirms exactly one guild chest immediately after the
  reinforced chest in the hammer's piece list, on both a fresh world and a
  saved-world reload. Menu position was also visually confirmed in the client.
- The final hammer-menu changes pass Release/native-fixture builds and the
  headless prefab-registration, recipe, coin-appearance, serialization and
  world-save checks.
- The reference setup succeeds through anonymous SteamCMD; a repeated setup
  skips installed downloads. Package metadata and the 256 × 256 PNG are valid,
  and the release ZIP contains only the intended package files.
- A native headless Valheim 1.0.16 fixture verifies prefab registration, chest
  recipe, dimensions, serialization of 12 iron and a damaged quality-3 iron sword
  with crafter/custom data, and saving the inventory with two distant chests.
- In earlier versions, restarting that fixture restores both unloaded chest
  records and their shared inventory. Removing the first preserves contents;
  removing the last emits exactly two item stacks, preserves the sword metadata,
  and clears the store.
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
- Version **1.0.2** passes the native compatibility fixture with the downloaded
  author's AzuAutoStore **3.1.7** and AzuCraftyBoxes **1.8.27** installed together.
  The real AutoStore hotkey handler respects hotbar exclusions and deposits once
  across two nearby guild access points. Crafty previews count the shared stock
  once; acknowledged building supply, its native ConsumeResources patch, and
  resuming native DoCrafting after material acknowledgement pass. The fixture
  delays commit replies by 0.5 seconds and verifies blocked player writes while
  pending, rejected/busy transfers without item loss, and ordinary chest writes.
  The native transfer fixture also passes with neither QoL mod installed.
  All 8 core synchronization tests pass. Live remote clients, upgrade/batch and
  one-ingredient recipes, favorites, and other optional storage mods remain
  manual checks below.
- Version **1.0.3** verifies that AutoStore's transfer effect fires once after
  acknowledgement, never while pending or for a skipped deposit; its configured
  chest ping and highlight components are created. The hammer HUD preview scope
  now includes same-frame cache invalidation. A portal-specific fixture checks
  **10/10 greydwarf eyes, 20/20 fine wood, and 2/2 surtling cores** from guild-only
  stock, the native CanBuild gate with a nearby workbench, and acknowledged
  transfer of all three resource types before a second build attempt. The second
  native TryPlacePiece call reaches the fixture spawn hook, creates exactly one
  actual portal prefab, and consumes its acknowledged resources once. All 8
  synchronization tests pass; the Release build has no warnings or errors.
- Version **1.0.4** passes the client-preview lifecycle regression: repeated
  authority updates with IsServer false and no authority manager leave the
  received preview intact. Authority reset also preserves it. Portal HUD counts,
  build checks, acknowledged resource supply, the placement spawn fixture and
  AutoStore effects pass afterward. All 8 core tests and the Release build pass.
- Version **1.0.5** passes single-click portal continuation through native
  UpdatePlacement after a delayed commit acknowledgement. The test recreates
  the placement ghost at the same logical target, then verifies one portal,
  one resource consumption, stamina and hammer durability costs, no duplicate
  on the following update, and no resource-ready/re-click message. Changing aim
  while waiting cancels placement and retains acknowledged resources. The
  existing client preview, crafting, AutoStore effect and rejection checks pass.
  All 8 core tests pass and the Release build has no warnings or errors.
- Version **1.0.6** adds native regressions for the three storage-adapter issues
  found in review. A recipe requiring two fish consumes two quality-2 fish from
  guild stock while retaining the player's quality-1 fish. Batch quantities,
  Leave One across qualities, insufficient matching stock without mutation,
  and existing player/ordinary-container ingredient choices pass. AutoStore
  deposits iron into guild storage and stone into ordinary storage with one
  hotkey, in both destination orders, with one effect per successful
  destination. Guild aliases, no-op/busy responses, single-item actions, and
  delayed open/commit responses pass. Sustained **1.2-second** preview replies
  keep build material counts available. A pre-commit preview cannot replace
  an acknowledged revision; dropped requests retry after a simulated timeout,
  and expired, reset-world and old-chest replies are ignored. The existing
  single-click portal, cancellation and client lifecycle checks also pass.
  All 8 core tests and the Release/native-fixture builds pass without warnings
  or errors.
- Version **1.0.7** adds five game-independent lifecycle cases, bringing the
  normal suite to **13 passing tests**. Native fault injection verifies that a
  seven-iron withdrawal remains seven after a throwing inventory notification
  and a duplicate ACK. Throwing no-op/rejection callbacks run once after session
  cleanup. A preview refresh fault does not reject or replay accepted items.
  A fault before local application retries locally without a second commit RPC.
  Destroyed access points, open timeouts and session reset complete cancellation
  once; late grants are released, and canceled opens resume ordinary AutoStore
  deposits. Earlier Harmony prefix failures leave UI, preview and consumption
  counters at zero. The complete compatibility fixture and single-click portal
  checks pass afterward, and Release/native-fixture builds have no warnings or
  errors. Injected exceptions are expected error-log entries in this fixture.
- Version **1.0.8** separates storage guards, scopes, previews, optional adapters,
  material planning and material supply. Native checks verify that nested staged
  inventories restore their enclosing/live inventories after a rule throws, and
  missing required adapter bindings install no partial hooks. The complete
  AutoStore/CraftyBoxes compatibility fixture passes, including quality rules,
  Leave One, destination ordering, transfer effects, slow/stale previews,
  transaction lifecycle faults, portal counts and single-click continuation.
  The native transport fixture also passes with both optional QoL mods absent,
  including unresolved player prefabs and the usual GUI/bulk/drag transfers.
  Builds derive plugin/assembly versions from the manifest and share game
  references. Four package tests accept a matching build and reject a changed
  manifest, replaced plugin/core DLLs, or a missing build receipt before writing
  an archive. Solution and native-fixture builds have no warnings or errors.
- Version **1.0.9** requires a workbench and adds 250 gold to the guild chest
  recipe. The native registration fixture verifies the workbench reference and
  exact costs: 20 fine wood, 10 iron, 2 surtling cores and 250 coins. Prefab
  registration, dimensions, item serialization and world saving pass. Release
  and native-fixture builds have no warnings or errors.
- The gold coin appearance change passes the native registration fixture:
  combined coin meshes retain vanilla gold materials, add no pickups, colliders
  or network components, and leave the vanilla chest and coin prefabs undecorated.
  Native opening and closing switches between the decorated lids correctly.
  Recipe costs, dimensions, serialization, world saving/reloading and final-chest
  spilling still pass, with exactly the two expected shared-item drops; all
  13 C# and 30 Python tests pass, and Release/native-fixture builds have no
  warnings or errors. Lighting and visible surface placement require the client
  checks below.
- Version **1.0.10** passes all 13 C# and 35 Python tests, including categorized
  release-note validation before downloads. Debug, Release/package, and
  native-fixture builds have no warnings or errors. The headless fixture loads
  Guild Chest 1.0.10 and passes decoration registration, lid switching, recipe
  costs, serialization, world save/reload, unloaded chest retention, and final
  spilling with exactly two item stacks and preserved metadata. Client-rendered
  color, lighting, clipping, and placement previews remain manual checks below.
- Version **1.0.11** passes all 13 C# and 38 Python tests, plus manifest and
  release note validation. Packaging regressions verify different ZIP timestamps
  for changed DLLs of the same size across releases and rebuilds, upgrades from
  the legacy fixed timestamp, and identical archives despite differing file
  modification times and packaging timezones.
- The coin mounting correction passes independent native mesh raycasts for all
  ten body coins and the five coins on each lid model. Their undersides sit
  slightly inside the actual mounting surfaces rather than above the mesh bounds.
  Native decoration registration, lid switching, recipe costs, serialization and
  world saving still pass. All 13 C# and 38 Python tests pass; Release and native
  fixture builds have no warnings or errors. Client lighting and visible clipping
  remain manual checks below.

Standard final-chest retention passes both runs of the native headless fixture
on Valheim 1.0.16. Destroying the only chest revokes its active lease without
changing inventory or revision, drops no shared items, and saves the contents
with no access points. Restarting that world preserves the iron
stack and sword metadata; a rebuilt chest grants access. Destroying the rebuilt
chest retains the items again. The Release plugin and native fixture compile
without warnings or errors. Live client behavior remains a manual check below.

The plugin-ID change passes isolated headless startup: BepInEx logs
`Loading [Guild Chest 1.0.11] (randenko.GuildChest)`, loads the dependent native
fixture, and creates `BepInEx/config/randenko.GuildChest.cfg`. Chest registration,
appearance, recipe, serialization and world saving pass. All 15 C# and 38 Python
tests pass, and the Release plugin and native fixture build without warnings or
errors.

Version **1.0.12** passes all 15 C# and 38 Python tests and categorized release-note
validation. Debug, Release/package, and native-fixture builds have no warnings or
errors. The headless fixture loads `Guild Chest 1.0.12` under `randenko.GuildChest`
on Valheim 1.0.16 (Steam build 25527701), with BepInExPack 5.4.2351 and Jotunn
2.30.2. Both standard fixture runs pass chest appearance, recipe, registration,
serialization, world save/reload, unloaded-chest retention, final-chest retention,
lease revocation, and preserved item metadata. Both dedicated retention runs pass
saving and restarting with no chests, retrieving the retained items after
rebuilding, and repeated final-chest destruction without shared-content drops.
The ZIP passes integrity and manifest-version checks and contains only the
intended package files. The user-confirmed hammer menu and remaining client
checks are recorded below.

The scripted host fixture disables dedicated-scene culling, structural wear on
its floating chest, the headless scene's GUI Update loop, and Game.Update's
player-spawn lifecycle. It binds the native
inventory grids explicitly. This validates handlers and RPCs, not rendering or
the normal player/input lifecycle.

The portal fixture runs native UpdatePlacement and TryPlacePiece with a
deterministic placement ghost and build table. It suppresses headless ghost
setup/raycast updates and explicitly tests replacing the ghost at the same
target. Its PlacePiece spawn hook instantiates the actual portal prefab because
a dedicated server has no platform-local user for native creator metadata.
Mouse raycasts, ordinary client placement metadata and visible effect rendering
still require a running client.

The client-lifecycle regression temporarily gives the native fixture the remote
client's authority state (no Store or manager, an active ZDOMan, and IsServer
false). It runs repeated Host.Tick calls and an authority reset, then checks
that the previously received preview and chest survive before reading portal
HUD counts. This exercises the client branch that the earlier host-only fixture
missed; it does not replace a live two-process multiplayer test.

Visual appearance, live controller input, and remote multiplayer clients remain
manual checks below; headless testing does not establish those results.

## Reusing the native fixture

The optional `tests/GuildChest.NativeSmoke` project is a BepInEx test plugin,
excluded from the release package and normal test command. Build it explicitly:

```bash
dotnet run --project build -- --target NativeSmoke
```

Install its DLL alongside Guild Chest in an isolated Valheim 1.x dedicated-server
installation. It runs only with `GUILDCHEST_SMOKE=1` and a world name beginning
with `GuildChestNative`. Set `GUILDCHEST_SMOKE_MARKER` to an absolute writable
path. The first run with that marker absent creates two distant chests and saves
sample contents; the second run of that world verifies persistence and final
destruction. It exits the server when finished and writes the marker or a
`.failed` file.

For final-chest retention, use a fresh disposable world and marker, and also set
`GUILDCHEST_SMOKE_RETENTION=1` for both runs. The first run destroys the only chest
while its lease is active, verifies no shared-content drops or inventory changes,
and saves the world with no chests. The second restores the items, rebuilds a chest
and opens it, then destroys it and verifies retention again with preserved metadata.

For the scripted host-transfer checks, use another fresh disposable world and
marker, and also set `GUILDCHEST_SMOKE_TRANSPORT=1`. Never install this test
plugin in a normal game or world: it creates/deletes fixture objects and exits
the process. A Linux headless server additionally needs its native runtime
libraries, including PulseAudio libraries; these are not part of the development-only
dev container. Run BepInEx with the normal server loader for your platform.

For the storage-mod fixture, install the author's **AzuAutoStore 3.1.7** and
**AzuCraftyBoxes 1.8.27** DLLs in that isolated installation, and additionally set
`GUILDCHEST_SMOKE_COMPAT=1`. This adds a 0.5-second commit-reply delay to exercise
the pending-inventory guards. The storage regressions additionally delay open
replies by 0.5 seconds, preview replies by 1.2 seconds, and drop one preview.
The timeout check advances the stored request timestamp by 11 seconds instead
of waiting ten seconds. Those DLLs and the fixture remain excluded from the
release ZIP.

The transaction lifecycle fixture deliberately throws from inventory-change
notifications, preview refresh, callbacks, local application preparation and
earlier Harmony prefixes. It also destroys access points during delayed opens
and simulates a 30-second opening timeout by advancing the request timestamp.
Expected injected exceptions must be followed by the transaction application,
cancellation and scope pass markers, then `NATIVE_SMOKE_COMPAT_PASSED`.

The adapter fixture checks nested staged inventory restoration and required-hook
validation before the compatibility flows. It emits
`NATIVE_SMOKE_ADAPTER_BINDINGS_PASSED`. Its deliberate missing-prefix lookup
produces a Harmony warning; no partial hooks may remain after that rejection.

## Automated and container checks

- Build the actual dev container Dockerfile.
- Restore and compile the full solution inside the container.
- Run the synchronization tests: global exclusivity, lease expiry/renewal,
  unauthorized mutations, stale revisions, retry ordering/idempotency, duplicate
  registrations/removals, unloaded-chest membership, retained inventory with no
  chests, rebuilding after save/reload, and
  separate/restored world stores.
- Package Release and inspect its ZIP for both plugin DLLs, README, icon,
  license, and manifest. Game and dependency DLLs must be absent.

## Gameplay checklist

The user has confirmed live gameplay for the 1.1.0 launch. The scenarios below
are retained for reference; checkboxes reflect individually documented results.

Record mode, game build, dependencies, result, and relevant logs for each run.
These checks require a running client; automated core tests cannot verify UI,
rendering, controller input, or a multiplayer player's inventory.

- [x] Client hammer menu (confirmed by the user): guild chest appears immediately
  after the reinforced chest, with an icon showing its gold tint and coin
  decorations, facing left with a viewing angle consistent with the vanilla
  chest icons.
- [ ] Return to the main menu and load a world again; confirm the hammer-menu
  ordering and icon in both the menu grid and selected-piece preview.
- [ ] Solo: recipe needs a workbench, 20 fine wood, 10 iron, 2 surtling cores and
  250 gold; the model has a warm gold tint, gold coin seals and studs on both
  faces, and five coins across the lid; vanilla chest and coin materials are
  unchanged.
- [ ] Inspect the coin details in daylight, torchlight and shade; open and close
  the chest and confirm that the coins follow the lid without floating or clipping.
  Check the placement ghost, rotation, damaged chest and save/reload appearance.
  Decorative coins cannot be picked up and add no collision or extra resource drops.
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
  Destroy the final chest while it is open: only construction resources drop,
  and the old session cannot move items. Rebuild a chest and retrieve all shared
  contents with their metadata intact. Repeat with a save/restart while there
  are no chests, and with repeated destruction/rebuilding. Inventory retention
  applies to every player without configuration.
- [ ] Save/restart with distant chests and contents; repeat in a second world
  and with the same character to verify world isolation.
- [ ] Missing/mismatched Guild Chest plugin prevents multiplayer entry.
- [ ] Repeat in player-hosted multiplayer and on a dedicated server with two
  modded clients. Verify ordinary chests still behave normally.
- [ ] AzuAutoStore: hotkey and single-item deposits with multiple guild access
  points, hotbar/favorite exclusions, existing-item restrictions, full storage,
  busy lease, denied wards, and latency. No player removal before acknowledgement.
- [ ] AzuCraftyBoxes: recipe previews count guild stock once; crafting/upgrades,
  batch crafting, one-ingredient recipes and building use acknowledged supplies.
  Test Leave One, YAML exclusions, changed recipe/station, full player inventory,
  and a second client consuming resources before the lease arrives.
- [ ] Unsupported ground/fuel/ore consumers skip guild storage while continuing
  to operate on ordinary containers. Repeat without either optional QoL mod.

## Recovery behavior

Missing item prefabs or unsupported serialization block access while
preserving stored bytes. Restore the appropriate item mod/version before retrying.
Unresolved prefabs in the player's inventory can remain there during unrelated
transfers. They cannot enter shared storage until the server has their prefab.
Transfer failures now return the server's reason to the client and log the
request operation, sender, chest, and sequence without logging the lease token.

Per-transfer acknowledgement receipts are retained in memory for the last 256
accepted operations. They resolve ordinary delayed/repeated messages, including
after closing or destruction, but are not a cross-restart transaction journal.
