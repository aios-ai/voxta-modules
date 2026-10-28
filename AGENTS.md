# Repository guidance

This is the active source repository for the installable modules under `Voxta.Modules.Aios.*`.

## Required release flow

- Treat each module's `module.json` as the source of truth for its ID, version, Voxta compatibility range, label, and author.
- Keep `Voxta.Sdk.Modules` references and committed `packages.lock.json` files synchronized with the manifest's Voxta range.
- Increment a module's version for every externally visible change. Use a SemVer suffix such as `-beta.1` when the release is not final.
- For a finished task that increments module versions, include publication in the completion flow unless the user explicitly asks to prepare only or not to publish.
- Before publishing, run the locked Release build, commit the complete change, push `main`, then run `./scripts/publish-module-releases.ps1 -Publish`.
- The release script creates one `<module id>/v<module.json version>` tag for every currently untagged module version and pushes the tags atomically. Never hand-create unqualified tags in this multi-module repository.
- A tag is not a completed release. Monitor the tag-triggered GitHub workflows and verify that each GitHub Release contains both the install ZIP and `voxta-module.json` before reporting completion.
- Do not overwrite or recreate an existing release tag. Use the `edit release` workflow to cap compatibility or yank an existing release.

The shared workflows in `.github/workflows` remain the authority for build, packaging, prerelease detection, and GitHub Release creation.

## Architecture

- [Tools foundation and migration contract](docs/ARCHITECTURE.md): shared tool catalog, SDK boundaries, Spotify and Hue tools-only integrations, OpenWeather tools/actions comparison, and migration coverage.
