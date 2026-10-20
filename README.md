# Voxta module template

Start a Voxta module with its release pipeline ready: push a tag and Voxta servers can install it from your GitHub
repository. The code is an empty module that registers with Voxta; replace it with yours.

More examples (text generation, text to speech, tools): [voxta-sdk-examples](https://github.com/voxta-ai/voxta-sdk-examples).

## Start

In Voxta, with an assistant to write it with you: **Workspaces → New workspace**, start from **Voxta module** (offered
once the Module Development module is installed), give it a name. Voxta clones this repository, renames it the way
`dotnet new` would, and checks this machine has the .NET SDK it needs. Chats in that workspace turn on module
development by themselves (`.voxta/workspace.json`): the assistant edits, loads the module into Voxta, reads why it
failed and reloads it.

With `dotnet new` (renames everything):

```
git clone https://github.com/voxta-ai/voxta-module-template
dotnet new install ./voxta-module-template
dotnet new voxta-module -n Weather -o voxta-weather
```

Or **Use this template** on GitHub, then replace `MyModule` in the folder, project, solution, namespaces, `ServiceName`
and `module.json`, and `My Module` / `my_module` in the label and augmentation key. CI fails until `MyModule` is gone.

Commit `packages.lock.json` with the rest: builds use exactly those packages, and CI fails without it. A package change
updates it on your next local build.

## Develop

1. `dotnet build`
2. In Voxta, **Add Modules → Add Custom Module**, add the project folder, **Apply**. It shows with a Dev badge.
3. Change, build (Voxta can keep running), **Apply**.

Load errors, including Voxta APIs your module uses that the running Voxta lacks, show on the Custom Modules page.

## module.json

| Field | |
|---|---|
| `id` | Unique; the assembly name |
| `version` | Your module's version; a release tag is `v` + this |
| `voxta` | The Voxta versions it runs on, e.g. `>=1.13.0` |
| `label`, `description` | How Voxta lists it |
| `platforms` | Only if not all, e.g. `["win-x64"]` |
| `python` | pip requirements, if any |

The SDK embeds it in the DLL and generates `ModuleInfo.Version` from it.

The `Voxta.Sdk.Modules` version is the oldest Voxta you support, `ExcludeAssets="runtime"`: Voxta provides it, so a
newer Voxta needs no rebuild. Raise it together with `voxta` when you need something newer: **Actions → bump voxta**
sets both, rewrites `packages.lock.json`, bumps the module's version, builds, and opens a pull request (enable
"Allow GitHub Actions to create and approve pull requests" under Settings → Actions). Merge it and tag.

## Release

1. Bump `version` in `module.json`, commit.
2. `git tag v0.2.0 && git push --tags`. A tag with a suffix (`v0.2.0-beta.1`) is a pre-release. A repository with
   several modules tags each one `<module id>/v0.2.0`.

The workflows here call Voxta's reusable ones ([voxta-module-actions](https://github.com/voxta-ai/voxta-module-actions)).
`release.yml` builds the module, attests it, and creates the GitHub release with two assets:
`<id>-<version>-any.zip` (what Voxta installs) and `voxta-module.json` (module.json as built, the zip's sha256).
`dotnet publish -c Release -p:VoxtaModulePack=dist` makes the same two files locally. A zip is at most 32 MB:
download CUDA, models and other large dependencies at runtime.

Users add your repository in **Add Modules → Module Registries** (`owner/repo`) and install from there. Voxta checks
each download against the sha256 in `voxta-module.json` and GitHub's own hash of the asset.

When a later Voxta breaks a release, cap its range without rebuilding: **Actions → edit release**, e.g.
`>=1.13.0, <1.15.0`. The same workflow yanks a release.

### In Voxta's own source

A repository Voxta trusts also publishes to Voxta's module source, listed in every Voxta without adding the repository:

1. Ask Voxta to trust it (they add it under **Admin → Module repos** in the Voxta portal, with its module ids).
2. Protect its release tags: Settings → Rules → Rulesets, a tag ruleset on `v*`.
3. Set the repository variable `VOXTA_PUBLISH` to `true` (`VOXTA_API_URL` picks another Voxta Cloud, e.g. staging).

Releases then publish under the job's GitHub token, which tells Voxta Cloud the repository, commit, tag and workflow:
no key to store. The repository has to be public, unless Voxta owns it. Deleting a release does not remove it from
Voxta's source: yank it.

## Verify a release

In a public repository every zip is attested by GitHub: it was built by this repository's `release.yml` from the
tagged commit.

```
gh attestation verify Voxta.Modules.MyModule-0.1.0-any.zip --repo <owner>/<repo>
```

The build is pinned (`packages.lock.json`, deterministic compiler output): rebuild the tag with
`dotnet publish -c Release -p:ContinuousIntegrationBuild=true` on the .NET SDK `voxta-module.json` names (`dotnetSdk`)
and the DLL matches the one in the zip. The zip itself differs (file times).

## License

MIT, for the template. Your module is yours: change `LICENSE` to whatever you publish it under.
