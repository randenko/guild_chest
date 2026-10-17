# Nexus Mods publishing

Guild Chest 1.1.0 was published manually on
[Nexus Mods](https://www.nexusmods.com/valheim/mods/4476). Later stable versions
use the separate **Nexus Mods** workflow after a reviewed GitHub release is
published. Both Nexus and Thunderstore consume the exact GitHub ZIP and checksum;
neither publisher rebuilds or repackages it. Nexus writes for 1.1.0 and earlier
versions are rejected in the helper, including manual retry mode.

Publishing is gated by `NEXUSMODS_PUBLISH_ENABLED`. Keep it `false` until the
authenticated baseline preflight passes from `main`; read-only manual preflight
is available while publishing is disabled.

## Configuration

GitHub **Settings → Secrets and variables → Actions** contains:

| Type | Name | Launch value / purpose |
| --- | --- | --- |
| Secret | `NEXUSMODS_API_KEY` | Owning account's API key; presence confirmed, value never retrieved. |
| Variable | `NEXUSMODS_MOD_ID` | `15749645078908`, API mod ID. |
| Variable | `NEXUSMODS_FILE_ID` | `8177514`, persistent main-file/update-chain ID. |
| Variable | `NEXUSMODS_MOD_PAGE_ID` | `4476`, numeric Valheim page ID. |
| Variable | `NEXUSMODS_PUBLISH_ENABLED` | `false` until the authenticated preflight passes after review. |

The public 1.1.0 file is `25735`, with composite file-version ID
`15749645100167`. These identify one version; keep `NEXUSMODS_FILE_ID` pointing
to the persistent file group when later versions are published.

Find IDs under **Advanced** on the Nexus Files tab or the Manage Files edit
menu, as described by the [official action](https://github.com/Nexus-Mods/upload-action#how-to-find-the-file-id--mod-id).
Generate keys in [Nexus API key settings](https://www.nexusmods.com/settings/api-keys).
Keys belong only in GitHub Actions secrets or a local environment; never put
keys in command arguments, logs, committed files, or chat messages.

## Activate after review

1. Review and merge the implementation through the usual process.
   The workflow is loaded from `main`; an unmerged local workflow cannot run
   with GitHub's stored secret.
2. In **Actions → Nexus Mods → Run workflow**, choose `main`, enter **v1.1.0**,
   and leave **Validate without publishing** enabled. This works while the
   publishing switch is false. It must recognize the manual upload, verify
   public archive identity and authenticated API target reads, and retain the
   manual baseline changelog without changing it.
3. Set repository variable **NEXUSMODS_PUBLISH_ENABLED** to **true** after that
   run succeeds. No additional API IDs or secret values are needed from the owner.
4. Publish the next real stable GitHub release through the normal draft/review
   procedure. Publishing it triggers both distribution workflows independently.
   A bugfix can use 1.1.1 and a feature can use 1.2.0; this change does not bump
   `manifest.json` or reserve the next version. Document these tooling changes
   under **Other changes** in that release's notes.
5. Check the Nexus result and test Vortex update detection from 1.1.0. Confirm
   both Guild Chest DLLs, dependencies, and a short chest interaction in a
   disposable world. The owner's earlier Vortex archive installation test is
   complete; the first automated upload and Nexus-associated Vortex update have
   not yet been exercised.

The original [main-page BBCode](nexusmods/page-description.bbcode) and
[launch field text](nexusmods/launch-fields.md) remain available for manual page
maintenance. The README and saved [YouTube description](media/youtube-description.txt)
now include the verified Nexus URL. Applying the new link to the live video
remains manual. The publisher does not edit page copy, images, permissions, or
previously published GitHub archives.

## Automated behavior

The workflow accepts stable `release: published` events when enabled. Drafts and
prereleases are excluded. Manual dispatch defaults to read-only preflight;
disabling that option also requires the publishing switch and a version greater
than 1.1.0. A shared concurrency group serializes all Nexus runs without cancelling
an earlier upload. Thunderstore has its own group and can be retried independently.
The GitHub token has `contents: read` and `actions: read` permissions.

Shared validation in `scripts/release_assets.py` checks the tag's ancestry on
`origin/main`, its manifest, published stable release status, unique ZIP/checksum
assets, SHA-256, archive allowlist, icon, release-specific links, and packaged
changelog against the notes at the tag. It uses tagged inputs even when current
`main` has moved on. Existing Thunderstore regression tests also exercise this
shared verifier.

The Nexus helper validates the Valheim page, API mod ID, and persistent main-file
ID, rejects duplicate versions and attempts to replace a newer version, and
prepares the exact ZIP for the official action. The action is pinned to
`98532f7670c27cbecc78fae03fa3e278147827e2` (`v1.0.0-beta.11`), reviewed against
its [definition](https://github.com/Nexus-Mods/upload-action/blob/98532f7670c27cbecc78fae03fa3e278147827e2/action.yml)
and [source](https://github.com/Nexus-Mods/upload-action/blob/98532f7670c27cbecc78fae03fa3e278147827e2/src/index.ts).
It creates a main-file version, archives its predecessor, enables mod-manager
downloads and the requirements popup, selects the new primary download, and
updates the page version.

The helper reconciles the file even if the action reports an error, since a
remote upload can succeed before its response is lost. Publication requires a
matching Nexus MD5 lookup, byte size, game, mod, and file-version identity plus
authenticated API agreement. A version listed by the authenticated API blocks
another upload even when GraphQL omits it or its intent artifact has expired.
Both views must agree on a single file before publication can be reconciled.
MD5 is Nexus's lookup identifier; the GitHub source
is verified with SHA-256. This does not claim an independent Nexus SHA-256 or
require a Premium-only automated download.

The live page uses **file-level requirements**. Metadata completion copies the
preceding file's version-range and DLC definitions to the new version when they
are missing. Equivalent definitions are left alone; conflicting editorial changes
fail without being overwritten. Before copying, preparation checks empty
definition sets against retained history back to the manual baseline. If an
earlier version has requirements that the predecessor lacks, or the history
needed to establish genuinely empty sets is unavailable, preparation stops
before creating upload inputs. This prevents an interrupted metadata update
from propagating missing requirements into subsequent releases, independently
of intent artifact retention.

Reconcile that predecessor's requirements using its saved intent or the last
complete version's definitions, then retry its metadata completion before
attempting the next release. Intentional removal of
a previously populated requirements set requires explicit policy review; the
helper will not silently treat it as complete. Nonempty editorial updates are
preserved. When using legacy mod-level requirements, the
helper leaves those page requirements alone. If dependency requirements need to
change in a later release, review the Nexus declarations alongside the tagged
manifest before publishing.

Changelogs preserve **Mod changes** and **Other changes** from the tagged release
notes. Wrapped Markdown bullets become entries; links point at the release tag.
The helper skips matching notes, appends missing notes once, and fails on
conflicting or partial notes. The official action does not submit the changelog,
so recovering file publication does not append a duplicate automatically.

## Preflight and retries

Use **Actions → Nexus Mods → Run workflow** with an existing stable tag. Leave
preflight enabled to validate without writing to Nexus. To retry a later release,
disable preflight. Matching existing files skip the upload action; verified files
with missing metadata can complete that metadata separately.

Public baseline verification also works locally without the Nexus key:

```bash
NEXUSMODS_MOD_ID=15749645078908 \
NEXUSMODS_FILE_ID=8177514 \
NEXUSMODS_MOD_PAGE_ID=4476 \
python3 scripts/publish_nexusmods.py v1.1.0 --preflight
```

When `NEXUSMODS_API_KEY` is available in the environment, preflight additionally
checks the authenticated v3 target. Public preflight cannot establish credential
or upload authorization. REST reads and GraphQL queries do not create upload
sessions or edit remote data. HTTP and GraphQL errors fail instead of being
interpreted as a missing version.

Before a file upload or changelog append, the workflow saves a 90-day Actions
artifact named `nexusmods-file-vX.Y.Z` or `nexusmods-notes-vX.Y.Z`. Each contains
only release/target metadata, a checksum, and requirements; it contains no secret.
These record an intended write, not proof of a successful one.

If an earlier intent exists and the corresponding result remains unconfirmed,
the retry fails instead of blindly submitting again. First wait for Nexus indexing
and rerun preflight. If the file or notes become visible, the normal retry can
verify them and continue without duplicating them.

If the result remains missing, inspect Nexus Manage Files/changelogs and the
original Actions logs. Only after confirming there is no completed or pending
write should a maintainer delete **that stage's intent artifact** from the
original Actions run and retry the same tag. Do not delete a notes intent merely
to bypass conflicting or partially visible text; reconcile the page first.
Successful intents can remain in place. Inspect unresolved attempts before their
90-day retention expires; artifacts are a recovery guard, not a permanent ledger.

The Actions summary reports the tag, source SHA-256, Nexus file-version ID,
verification method, requirements and changelog status, and listing URL. A failed
Nexus run does not change the GitHub release or rerun Thunderstore.

## Recorded launch validation

The owner's manual page publication and GitHub configuration were confirmed on
2026-10-17. Public GraphQL reads identify Guild Chest 1.1.0 as the primary main
file, with its initial changelog and a verified scan status. The actual helper's
public baseline preflight passes against both live services:

- GitHub ZIP size: **65,350 bytes**.
- GitHub SHA-256: `133b8fe196e45c49fc0d984d07b9f948a070ca25557fe4b7eab8baa70bf7dbf3`.
- Matching Nexus MD5 lookup: `0093945ce82ae6216233e1abd450930f`, resolving to the
  expected Valheim mod and exact initial file version.

Automated coverage exercises version guards, public and authenticated identity,
archive conflicts, prior ambiguous attempts, delayed hash indexing, exact local
bytes, file/DLC requirement preservation, interrupted predecessor recovery,
authenticated/public API disagreement, lost responses, and changelog recovery.
Workflow syntax is checked with actionlint. Authenticated preflight remains a
post-review GitHub Actions check because GitHub cannot reveal stored secret
values to a local checkout. Upload authorization and Vortex update detection
remain pending the next real release.

API references: [Nexus v3](https://api-docs.nexusmods.com/) and
[GraphQL file hashes](https://graphql.nexusmods.com/#query-fileHashes).
