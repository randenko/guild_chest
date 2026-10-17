# Nexus Mods 1.1.0 launch fields

Use these alongside the [launch checklist](../NEXUSMODS.md). Copy only each
field's fenced contents, without the backticks. The main page description is
provided separately in [BBCode](page-description.bbcode).

## Mod name

```text
Guild Chest
```

## Version

```text
1.1.0
```

## Summary

```text
One world. One shared stash. Build gold-decorated Guild Chests to access the same 32-slot inventory across your Valheim world. Supports solo and multiplayer, with Vortex-compatible installation.
```

## Author

Use the Nexus account's actual author/display name. If a separate author field
allows a project credit, `Guild Chest contributors` matches the repository's
copyright notice. No new team account is required for the launch.

## Main file name

```text
Guild Chest 1.1.0
```

## Main file description

```text
Complete Guild Chest 1.1.0 release for clients and hosts/servers. Install on every client and the host/server using matching Guild Chest versions. Requires BepInExPack Valheim 5.4.2351 and Jötunn 2.30.2, available separately. Includes GuildChest.dll and GuildChest.Core.dll together under BepInEx/plugins/GuildChest. Install with Vortex or extract the ZIP's BepInEx folder into your Valheim installation. Back up your world before installing or updating.
```

## Initial changelog

Select version `1.1.0`. Paste these entries into separate changelog lines if the
editor uses an entry list. This is a Nexus introduction, based on the already
published GitHub version; it does not replace that release's checked-in notes.

```text
Initial Nexus Mods release of Guild Chest, using the existing GitHub 1.1.0 archive.
All Guild Chests in a world share one 32-slot inventory, with normal ward permissions and one player accessing it at a time.
Shared contents remain in the world save when the last chest is destroyed and can be accessed by rebuilding a Guild Chest, including after a restart.
Version 1.1.0 removes legacy spill-recovery handling for older saves; current final-chest retention remains unchanged.
The GitHub release ZIP was installed through Vortex and tested successfully by the author.
```

## Credits

```text
Guild Chest contributors.
BepInEx team for the plugin framework.
Jötunn / ValheimModding contributors for the Valheim modding library.
Iron Gate Studio for Valheim.
```

## Permissions / custom permission text

Use the form's MIT/custom option where available. Match checkboxes for reuse,
modification, distribution, and commercial use to the license for Guild Chest's
original work. This text does not grant rights to Valheim assets or dependency
code. The license file remains authoritative.

```text
Guild Chest's original code and included documentation are released under the MIT license. You may use, copy, modify, merge, publish, distribute, sublicense, and sell copies subject to retaining the copyright and permission notice as required by that license. The software is provided without warranty. Third-party dependencies and Valheim assets retain their own licenses and ownership; no additional rights to those materials are granted here.

License: https://github.com/randenko/valheim-guildchest/blob/main/LICENSE
```

## Source / homepage link

```text
https://github.com/randenko/valheim-guildchest
```

## Bug reports and feature requests / pinned post

This version uses BBCode for the Nexus post editor.

```bbcode
[b]Bug reports and feature requests are tracked exclusively through GitHub Issues.[/b]

[url=https://github.com/randenko/valheim-guildchest/issues/new?template=bug_report.yml]Report a bug[/url] | [url=https://github.com/randenko/valheim-guildchest/issues/new?template=feature_request.yml]Request a feature[/url]

For bugs, include your Valheim and Guild Chest versions, dependency versions, whether you use a dedicated server, reproduction steps, and relevant BepInEx/LogOutput.log excerpts. Install Guild Chest and both required dependencies on every client and the host/server, with matching Guild Chest versions.
```

## Video URL

```text
https://www.youtube.com/watch?v=3EhbnWifGQA
```

## Optional support URL

```text
https://ko-fi.com/randenko
```

Use the site's optional donation/support field if offered. No donation or
endorsement request is needed to publish the page.

## Media captions

| File in `docs/media/presentation/` | Caption |
| --- | --- |
| `01_cover.png` | One world. One shared stash. |
| `02_inventory_view.png` | One shared inventory, with room for metals and supplies. |
| `03_hammer_menu_with_recipe.png` | Find the gold chest in the hammer menu; the recipe is shown below it. |
| `04_build_guild_chest_with_recipe.png` | Add an access point wherever you build. |

## Information to record after publishing

- Actual public Valheim mod page URL and numeric page ID.
- API mod ID and persistent main-file ID from Advanced; record the initial
  file-version ID separately if the UI provides it.
- Main file version `1.1.0`, downloaded SHA-256, and Nexus processing status.
- Vortex installation through the Nexus button, page association, and reported
  version. The existing GitHub drag-and-drop test is already confirmed.

API keys belong only in GitHub Actions secrets, not in this document.
