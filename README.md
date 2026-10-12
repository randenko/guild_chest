# Guild Chest

<!-- CI covers tests and packaging on main pushes. Host fixed badge images in this
repository to avoid third-party API lookups and image-proxy timeouts. The release
badge links to the latest published release without hard-coding a version. -->
[![CI](https://github.com/randenko/guild_chest/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/randenko/guild_chest/actions/workflows/ci.yml?query=branch%3Amain+event%3Apush)
[![Latest release](https://raw.githubusercontent.com/randenko/guild_chest/main/docs/badges/release.svg)](https://github.com/randenko/guild_chest/releases/latest)
[![MIT License](https://raw.githubusercontent.com/randenko/guild_chest/main/docs/badges/license.svg)](https://github.com/randenko/guild_chest/blob/main/LICENSE)

A Valheim 1.x mod that makes every guild chest in a world an access point to
one shared inventory. Supports single-player, player-hosted multiplayer, and
dedicated servers. The mod and its dependencies must be installed on every
client and the host, with matching Guild Chest versions.

## Gameplay

- Build a **Guild Chest** with the hammer near a workbench, using **20 fine wood,
  10 iron, 2 surtling cores, and 250 gold**. Its warm gold-tinted reinforced chest
  carries large gold coin seals, smaller coin studs, and a row of coins on the lid.
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
  the **last** chest keeps all shared items in the host/server's world save.
  Rebuild any guild chest in that world to access them again, including after
  saving and restarting with no chests. Shared contents never drop on destruction.
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
   dotnet run --project build -- --target Compile
   dotnet run --project build -- --target Test
   ```

5. Run **Package Release**, or:

   ```bash
   dotnet run --project build -- --target Package
   ```

The release ZIP is written to `artifacts/GuildChest-1.0.11.zip`, visible in the
host workspace. Container rebuilds retain downloaded references in `.local`
and NuGet packages in the `guild-chest-nuget` Docker volume. Post-create downloads
missing references and restores NuGet packages; updating existing game references
still requires the explicit `setup --update` command below.

The dev container includes Codex CLI, installed as `vscode` using
[OpenAI's official shell installer](https://learn.chatgpt.com/docs/codex/cli).
Run **Dev Containers: Rebuild Container** to add it to an existing container,
then run `codex` in the container terminal. Your host `~/.codex` directory is
mounted read-write at `/home/vscode/.codex`, sharing authentication, configuration,
and session history. Sign in when prompted if you have not already done so.
The CLI and daemon packages are installed with the official installer and kept
in the `guild-chest-codex-packages` Docker volume. Daemon runtime files use the
`guild-chest-codex-daemon` and `guild-chest-codex-control` volumes. These mount over
the corresponding directories inside `.codex`, keeping executable symlinks,
process IDs, and sockets local to
the container while sharing the rest of your Codex state with the host.

Codex's Linux sandbox uses Bubblewrap to create nested namespaces. The container
installs `bubblewrap` explicitly and starts with `--security-opt seccomp=unconfined`
to allow the namespace calls that Docker's default seccomp filter blocks. This
disables Docker's syscall filtering for this dev container; Codex still enforces
its own filesystem and network sandbox. After **Dev Containers: Rebuild Container**,
verify the sandbox from the container terminal with:

```bash
codex sandbox -- true
```

The command should exit successfully. A `bwrap: No permissions to create new
namespace` error means the container or host still blocks namespace creation.
See [OpenAI's sandbox guidance](https://learn.chatgpt.com/docs/agent-approvals-security#os-level-sandbox)
for container requirements.

The dev container mounts the Linux host's `~/.ssh` directory read-only at
`/home/vscode/.ssh`, so Git can use your existing GitHub SSH key. After changing
the container configuration, run **Dev Containers: Rebuild Container** to apply
the mount. Verify authentication inside the container with `ssh -T git@github.com`
before pushing. GitHub's successful authentication greeting exits with status 1.
If your key has a passphrase, SSH prompts for it unless an agent has it loaded;
VS Code also forwards a running host SSH agent automatically. Because the mount
is read-only, add any missing GitHub host-key entry to `~/.ssh/known_hosts` on
the host by running `ssh -T git@github.com` there first.

The dev container also includes GitHub CLI (`gh`) from
[GitHub's official apt repository](https://github.com/cli/cli/blob/trunk/docs/install_linux.md#debian).
Run **Dev Containers: Rebuild Container** to add it to an existing container.
Authenticate once inside the rebuilt container with `gh auth login` and verify
with `gh auth status` before using commands such as `gh pr list` or `gh pr checks`.
The `guild-chest-gh` Docker volume persists `/home/vscode/.config/gh` across
container rebuilds, so subsequent rebuilds retain your login. The post-create
command restores ownership for the container user if its UID changes. Deleting
this volume removes the saved login. In this container, GitHub CLI falls back to
[storing its token in a plain-text configuration file](https://cli.github.com/manual/gh_auth_login)
when no system credential store is available; the directory is restricted to the
container user and stays outside the repository.

The C# extension is configured to handle the SDK-style `net48` plugin. If
VS Code opens before automatic setup finishes, wait for it to complete and then
run **Developer: Reload Window** to refresh unresolved game symbols. If reference
setup fails, retry with **Tasks: Run Task → Setup References** or
`dotnet run --project build -- --target Setup`.

To debug automated tests, start **Debug Test Host**, wait for its process ID,
then select **Attach to .NET test host** in the Run and Debug panel and pick that
`testhost` process. Gameplay runs in your separately installed Valheim client
or server; inspect its `BepInEx/LogOutput.log` for mod diagnostics.

## References and builds

The plugin targets **.NET Framework 4.8**, its game-independent core targets
**.NET Standard 2.0**, and tests run on **.NET 10**. Linux builds obtain the
Framework reference assemblies from NuGet. The dev container image is pinned
by digest, and NuGet lock files are checked in.

Build orchestration uses [NUKE](https://nuke.build/) in the standalone
`build/_build.csproj` project. The SDK restores the pinned runner dependencies
in locked mode; no global NUKE tool or shell wrapper is required. List targets
and parameters with:

```bash
dotnet run --project build -- --help
```

| Target | Behavior |
| --- | --- |
| `Setup` | Fetch missing references and pinned mod dependencies; add `--update-references` to refresh. |
| `Restore` | Set up references and restore the product solution in locked mode. |
| `Compile` | Set up, restore, and build Debug; use `--configuration Release` for Release. |
| `Test` | Restore the test project and run C# and Python tests without game downloads. |
| `Package` | Build Release and create the ZIP after validating the build receipt. |
| `NativeSmoke` | Restore and compile the optional native fixture in Release. |
| `Compatibility` | Run tests, fetch fresh references, build Debug, package Release, and compile the native fixture. |
| `ValidateRelease` | Check `--release-tag` against the manifest, verify main-branch ancestry, and validate categorized release notes. |
| `Checksum` | Build the package and write its `.zip.sha256` file. |
| `Release` | Validate the tag, test, fetch fresh references, build Debug, package Release, and write the checksum. |

Multiple targets share dependencies within one invocation, for example
`dotnet run --project build -- --target Compile Package`. Parameters after the
`--` belong to NUKE; `dotnet run --configuration Release` before it selects the
runner's configuration, while `--configuration Release` after it selects the
product build and test configuration. Packaging always builds Release.
`Release` and `Compatibility` use Debug for their solution build by default;
neither target runs gameplay checks or publishes a release.

`ValheimManagedDir` defaults to the downloaded server's
`.local/valheim-server/valheim_server_Data/Managed` directory. Override it when
building against a different client or server installation:

```bash
dotnet run --project build -- --target Compile --valheim-managed-dir /path/to/Managed
```

This uses the supplied assemblies without running SteamCMD; setup fetches only
missing pinned mod dependencies. Relative paths resolve from the repository
root. With an override, `--update-references` refreshes the mod dependencies and
leaves the supplied game installation alone. `Release` and `Compatibility`
require the default reference source so they can validate fresh downloads.

Game downloads and dependency DLLs are ignored by Git and excluded from the
release ZIP. `.local/references.json` records the Managed directory, dependency
versions, and assembly SHA-256 hashes. Default downloads also record the Steam
manifest/build ID; custom references use absolute hash keys for external files
and do not claim the downloaded server's Steam manifest. To update the game
references deliberately and refetch pinned mod dependencies:

```bash
dotnet run --project build -- --target Setup --update-references
```

Compile and package targets fetch missing references automatically and reuse
existing downloads. Core tests do not run reference setup. The original game
assemblies are referenced directly; Harmony accesses the few private fields and
methods needed by the mod.
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

## GitHub automation and releases

Pull requests and pushes to `main` run two independent CI checks using the same
NUKE targets as local development:

- **tests** invokes `Test`, which restores the core test project in locked mode
  and runs the C# and Python suites without downloading Valheim. Python tests
  also exercise the real NUKE target graph against disposable repositories and
  recording tools, checking dependency sharing, release gates, and failure handling.
- **build-package** invokes `Compile Package`, which sets up game references,
  restores the solution in locked mode, builds Debug, and produces the
  installable Release ZIP.

Open a run under **Actions → CI** to download `guild-chest-package`, test
results, and build diagnostics. Artifacts are retained for 14 days. The package
artifact wraps the installable `GuildChest-<version>.zip`; extract that outer
artifact before installing. These CI packages are development builds, not
published releases. Require the `tests` and `build-package` checks in the `main`
branch ruleset to block merging failing changes.

CI caches NuGet packages by lock-file hashes and game references by setup
script, manifest, and UTC week. A cache miss downloads roughly 2.2 GB; later
runs reuse the reference assemblies. Builds run in the same pinned .NET base
image as the dev container, with build prerequisites but without its Codex
installation or host credential mounts.

**Valheim compatibility** runs every Monday at 06:17 UTC and can be started
manually from Actions through the `Compatibility` target. It always checks
`main` against fresh game references, runs the automated suites, builds/packages
the plugin, and compiles the optional native fixture. Its diagnostics include
setup/build logs, test results, and
`references.json` with the Steam build and assembly hashes. Compilation does
not establish native runtime or gameplay compatibility; use the validation
checklist for those checks. Configure GitHub Actions notifications to receive
failure notifications. Dependabot also opens weekly grouped update PRs for
NuGet packages and pinned GitHub Actions; updates require review and passing CI.

To release a new version:

1. Update `version_number` in `manifest.json`, update the relevant documentation,
   and write `docs/releases/vX.Y.Z.md` with `## Mod changes` first and
   `## Other changes` second. Put player-facing behavior, visuals, fixes, and
   compatibility in Mod changes; put tooling, CI, documentation, and development
   environment changes in Other changes. Include at least one bullet per section,
   using `- None.` when a category has no changes. Review changes since the previous
   release and include a comparison link after the sections. Commit the notes
   and merge the change into `main` after CI and relevant gameplay checks pass.
2. Tag the merged commit with the exact manifest version and push that tag:

   ```bash
   git tag -a vX.Y.Z -m "Guild Chest X.Y.Z"
   git push origin vX.Y.Z
   ```

3. The **Release** workflow invokes the NUKE `Release` target, which verifies
   that the tag is exactly `v<manifest version>` and its commit belongs to
   `main`, and checks that the release notes have both sections in order with
   a bullet in each. It reruns tests, downloads fresh references
   without using reference or NuGet caches, builds Debug, and packages Release.
4. After validation succeeds, it creates a **draft GitHub release** containing
   the checked-in release notes, the installable ZIP, and a `.zip.sha256` checksum.
   Review the notes, package, reference build, and relevant gameplay results,
   then publish the draft from GitHub's Releases page.

Stable `vX.Y.Z` tags are supported; prerelease tags are rejected. Validation
failures create no release. Rerunning a successful workflow can replace assets on
an existing draft, but refuses to modify a published release. No external
publishing token is needed: only the final draft job receives `contents: write`
through GitHub's built-in token. Publishing to Thunderstore remains a manual
step. GitHub workflows do not bump versions, create tags, or publish drafts
automatically.

To exercise release validation and packaging locally without creating or
publishing a GitHub release:

```bash
dotnet run --project build -- --target Release --release-tag v1.0.11
```

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
- `build`: standalone NUKE build runner and shared game-reference properties.
- `scripts`: Python reference setup, release validation, and deterministic packaging.
