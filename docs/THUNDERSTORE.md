# Thunderstore publishing

The root README is the canonical player listing. Packaging converts its relative
document links to GitHub `blob/vX.Y.Z` URLs and its images to raw GitHub `vX.Y.Z`
URLs. `CHANGELOG.md` comes from `docs/releases/vX.Y.Z.md`. Media is tracked in the
repository and is excluded from the mod ZIP. Review the presentation images and
the [YouTube description](media/youtube-description.txt) before release.

## Review and publication

1. Follow the [development release procedure](DEVELOPMENT.md): review, commit,
   merge, tag, and run the existing Release workflow. It validates, builds, and
   creates a GitHub **draft**, without publishing it.
2. Review the notes, ZIP, checksum, listing, screenshots, reference-build details,
   and relevant gameplay checks before manually publishing the GitHub draft.
3. GitHub's `release: published` event starts the separate Thunderstore workflow
   when repository variable `THUNDERSTORE_PUBLISH_ENABLED` is exactly `true`.
   It accepts stable published releases, validates the tag's ancestry on
   `origin/main`, and compares the ZIP manifest with the manifest at that tag.
   It downloads the release's exact ZIP and SHA-256 asset, verifies checksum,
   archive structure, icon, changelog, dependencies, and Valheim categories.
4. Upload uses official **tcli 0.2.4**, pinned by asset SHA-256, with `--file`.
   It never rebuilds or repackages the archive. Metadata comes from the ZIP
   manifest; categories are `mods`, `building`, and `utility` in `valheim`.
5. The Actions summary reports the listing URL and verified archive SHA-256.
   After successful publication, check the listing, install in a clean mod-manager
   profile, verify dependency resolution and both Guild Chest DLL versions, and
   compare the downloaded archive checksum. Then add the **verified** listing URL
   to both the saved description and the live YouTube description manually.

Guild Chest 1.1.0 is published on
[GitHub](https://github.com/randenko/valheim-guildchest/releases/tag/v1.1.0) and
[Thunderstore](https://thunderstore.io/c/valheim/p/Paper_Street_Software/GuildChest/).
The published archives match byte for byte. A clean official tcli profile installs
Guild Chest 1.1.0, BepInExPack 5.4.2351, and Jötunn 2.30.2; the listing renders
all four screenshots and the player README. See [validation](VALIDATION.md).

The user has confirmed live gameplay, the Ko-fi link, and applying the updated
YouTube description, including the verified Thunderstore listing URL. The 1.1.0
launch checks are complete.

Configuration uses repository variable `THUNDERSTORE_NAMESPACE` (launch value:
`Paper_Street_Software`) and secret `THUNDERSTORE_API_TOKEN`, supplied to tcli as
`TCLI_AUTH_TOKEN`. Presence was verified during planning and upload authorization
was verified by the successful 1.1.0 upload. The GitHub token has read-only
repository permissions in this workflow.

Set the **Actions repository variable** `THUNDERSTORE_PUBLISH_ENABLED` to `true`
to permit publishing, or `false` to turn it off. An unset variable or any other
value also disables publishing. This matches the Nexus Mods publishing switch;
it is a repository variable, not a secret or a GitHub Environment setting.
The value is passed into the helper's environment for an independent upload
check, including direct `--upload` invocations.

## Read-only preflight and retries

In **Actions → Thunderstore → Run workflow**, enter an existing stable release
tag. Leave **Validate without uploading** enabled for GET-only preflight. To retry
publication, disable that option and set `THUNDERSTORE_PUBLISH_ENABLED=true`.
Read-only preflight remains available while publishing is disabled. A manual
upload request while disabled fails with an explicit error before invoking tcli.
All uploads share one concurrency group and do not cancel earlier uploads.

The helper defaults to preflight. An upload requires an explicit `--upload` and
`THUNDERSTORE_PUBLISH_ENABLED=true` in the environment:

```bash
python3 scripts/publish_thunderstore.py v1.1.0 --preflight
```

Set the namespace and token in the environment; never put tokens in arguments
or commit them. Preflight checks presence and configuration, but does not test
upload authorization. It cannot fully validate an unpublished draft. New releases
must use the current package structure, including `CHANGELOG.md`; historical
1.0.12 archives are not rewritten or uploaded by this preparation.

Before uploading, the helper looks up the exact Thunderstore version and hashes
its downloadable ZIP. Identical bytes succeed without another upload; differing
bytes fail and require a new version. API errors other than 404 fail rather than
being treated as an absent version. After invoking tcli, it polls for the published
version and verifies its bytes even when tcli exits with an error. Metadata requests
use a fresh query key to avoid a cached pre-publication 404 from Thunderstore's CDN.
An interrupted or unconfirmed attempt remains retryable: the next run checks existing bytes
again before submitting. If publication succeeded but is not visible yet, wait
for API visibility before retrying. Failed checks leave the GitHub release intact.

The live endpoints and CLI configuration were checked against the
[official tcli 0.2.4 source](https://github.com/thunderstore-io/thunderstore-cli/tree/0.2.4),
[official release asset](https://github.com/thunderstore-io/thunderstore-cli/releases/tag/0.2.4),
and [Thunderstore API documentation](https://thunderstore.io/api/docs/).
