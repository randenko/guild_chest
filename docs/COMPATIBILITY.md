# Storage-mod compatibility

Guild Chest uses one authoritative server inventory, shared by every access
point. A local `Container` inventory is not a writable copy of that storage.
Writing to it and calling vanilla `Container.Save` bypasses the server protocol;
this caused the lost AutoStore deposits in versions 1.0.0 and 1.0.1.

Version **1.0.2** protects those local inventories and adds optional adapters.
Install both Guild Chest DLLs on all clients and the server, with matching
versions, and restart. The save format remains unchanged. Items lost by earlier
versions cannot be reconstructed by this update.

Version **1.0.3** restores AutoStore's chest ping/highlight and the native
item-transfer effect after acknowledgement. Skipped or rejected deposits do not
play success effects. It also includes the hammer build HUD in the resource
preview scope and clears same-frame resource-count caches when that scope or
the acknowledged stock changes, so a cached zero does not hide guild materials.

Version **1.0.4** fixes a multiplayer-client lifecycle error: the server-authority
update previously reset the crafting preview on every client frame because
clients never initialize the authority's world manager. Authority updates now
return immediately on clients, and authority resets no longer clear previews.
Preview cleanup belongs to world shutdown. This fixes the persistent zero
hammer-menu counts that remained on clients despite the host fixture passing.

Version **1.0.6** fixes mixed-quality one-ingredient recipes, resumes AutoStore's
remaining destinations after a guild transfer, and accepts preview replies
that take longer than the polling interval. The shared-store save format and
single-click building behavior remain compatible with earlier saves.

## AzuAutoStore

The adapter targets **AzuAutoStore 3.1.7**, obtained from its author's Hexium
release. It handles the player inventory hotkey and single-item deposit path.
The mod's own rules run against staged inventories: equipped items, favorites,
hotbar/quick-slot exclusions, YAML restrictions, and existing-item requirements
still apply. A server acknowledgement commits the shared inventory before the
player's real inventory is updated. Busy, full, invalid or inaccessible storage
does not remove items from the player.

Multiple nearby guild chests represent one destination. Actions that include
guild storage retain AutoStore's destination order. Processing pauses for the
guild lease and commit acknowledgement, then resumes remaining ordinary
containers automatically, including after a denied or successful no-op guild
transfer. Ordinary deposits run through AutoStore's original storage methods.
Each successful destination receives its transfer feedback once. Single-item
actions retain the selected item's identity; an item that replaces it in the
same slot is not deposited. Depositing while a guild UI session is open uses
that session when the destination matches it.

Ground-drop automation is **excluded for guild chests** in this version. It
requires a separate server-authoritative item-drop transaction; a player
transfer cannot safely acknowledge deletion of a world drop. Ground automation
continues to work for ordinary containers.

## AzuCraftyBoxes

The adapter targets **AzuCraftyBoxes 1.8.27** from the author's Hexium release.
Nearby guild contents are polled for recipe/resource previews and counted once,
even with several nearby guild chests. The adapter respects the mod's container
selection, pull restrictions and Leave One setting.

For crafting and upgrades, required guild resources are first transferred into
the player's inventory through the acknowledged protocol. Crafting resumes
after acceptance if the recipe, upgrade item, quantity and station are still
the same. If those change, the transferred resources remain in the player's
inventory. Free player inventory space is needed for the supplied materials.

One-ingredient recipes retain any usable player or ordinary-container choice
selected by Valheim and CraftyBoxes. If guild stock is needed, the transfer
supplies enough of one ingredient quality for the entire recipe or batch.
Different qualities are not combined to satisfy that cost. Leave One reserves
one ingredient in the shared container, using CraftyBoxes's container rule.
Counts and restrictions are checked again against the leased inventory before
the transfer is submitted.

From **1.0.5**, one build click transfers missing guild resources and then
automatically continues through Valheim's normal placement loop. There is no
resource-ready message or second click. The network acknowledgement still
precedes placement. Ordinary storage can supply remaining resources through
CraftyBoxes as usual.

The continuation is cancelled if you change the selected piece, tool, aim or
rotation, move away, leave build mode, open piece selection, die, teleport, or
lose a valid placement before it resumes. Rejected transfers still show their
error. Materials already acknowledged remain in your inventory if placement is
cancelled. The normal placement loop retains its stamina, hammer durability,
skills, recent-piece history and effects.

Resource previews keep one request in flight per selected guild access point,
with a minimum one-second polling interval and a ten-second request timeout.
Preview replies can take longer than one second without being superseded by
another poll. Late replies for an expired request, another access point or a
previous world are discarded. Inventory revisions prevent an older preview
from overwriting stock confirmed by a transfer acknowledgement. Previews still
expire three seconds after their last accepted update; the server validates
the current inventory when a transfer starts.

CraftyBoxes' other direct consumers, including fuel/ore feeding and Epic Loot
providers, are excluded from guild storage until they have their own adapters.
Guild resources are exposed to the crafting/building previews, not to arbitrary
consumption. Ordinary containers retain those features.

## Range and permissions

`BepInEx/config/com.randenko.guildchest.cfg` contains:

```ini
[Compatibility]
Automation range = 20
```

The server enforces this maximum distance to the selected access point for
automation and previews. Values from 1 to 100 metres are accepted. The QoL mod's
configured range can restrict it further. Configure the server and clients
consistently when changing it. Normal chest interaction retains its original
range. Ward permissions and the single-player lease still apply.

## Other storage mods

Unadapted native inventory writes are blocked, and closed guild containers expose
no writable capacity. This prevents ordinary API callers from treating an
access point as local storage. Mods that change private inventory lists or stack
fields directly need explicit integration; universal compatibility cannot be
guaranteed. Do not disable the server version check to mix protocol versions.

Other mod authors can reference `GuildChest.dll` as an optional dependency and
use the public client API on Unity's main thread:

```csharp
bool started = GuildChestStorage.TryTransfer(chest,
    (playerStage, sharedStage) => sharedStage.StackAll(playerStage),
    (accepted, reason) => { /* Update UI only after acknowledgement. */ });
```

`IsGuildChest(Container)` identifies access points. `TryTransfer` acquires the
lease, obtains the current snapshot, runs the action on disposable clones, and
submits the conserved transfer. `false` means the request could not start and no
completion callback follows. If it starts, the callback reports rejection or
acceptance, including a successful no-op. Actions must only transfer items
between the supplied inventories. Do not keep their references, mutate real
inventories, create gameplay objects, or consume resources in that action.
Exceptions discard staged changes. While a transfer awaits acknowledgement,
native player inventory mutation methods are blocked.

The API does not make world/character saves atomic and does not recover earlier
lost items. See [validation](VALIDATION.md) for tested paths and remaining live
multiplayer checks.

## Upstream releases inspected

- [AzuAutoStore 3.1.7](https://valheim.hexium.gg/mods/Azumatt/AzuAutoStore)
- [AzuCraftyBoxes 1.8.27](https://valheim.hexium.gg/mods/Azumatt/AzuCraftyBoxes)

Optional hooks use reflection; neither third-party DLL is bundled or required.
Future mod versions can change their internal methods. Missing hooks are logged,
and unadapted writes remain blocked rather than using vanilla local storage.
