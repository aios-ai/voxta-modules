# Voxta modules by aios

Installable Voxta modules maintained by aios. Add `aios-ai/voxta-modules` under
**Add Modules → Module Registries** in Voxta Server to discover published modules.

Each module has its own project directory, `module.json`, version, dependencies, and release history. A push to
`main` builds every module; a module-qualified version tag builds and releases only the named module.

## Modules

### Philips Hue

`Voxta.Modules.Aios.PhilipsHue` controls Philips Hue lights, smart plugs, rooms, zones, scenes, color, and brightness
from Voxta chats. It can also expose a configured light to a character for mood and emotion-driven changes.

The module itself is free. It requires Voxta `1.13.0` or newer and a Philips Hue Bridge reachable on the local
network. Initial setup asks you to press the bridge link button.

### OpenWeather

`Voxta.Modules.Aios.OpenWeather` adds current weather, forecasts, air quality, and generated weather maps to Voxta
chats. It supports configurable locations, units, reported details, and action-inference matching.

The module itself is free. It requires Voxta `1.13.0` or newer and an OpenWeather API key; OpenWeather offers a
rate-limited free plan.

### Spotify Control

`Voxta.Modules.Aios.Spotify` adds Spotify playback control to Voxta chats. It connects through Spotify OAuth and
exposes actions for playback, search, queueing, volume, seeking, shuffle, repeat, favorites, playlists, and devices.

The module itself is free. It requires Voxta `1.13.0` or newer, a Spotify Premium account, and a Spotify Developer
application that provides a Client ID and Client Secret.

## Develop

1. Run `dotnet build Voxta.Modules.slnx` to build the repository, or build a module project directly.
2. In Voxta, open **Add Modules → Add Custom Module**, select the module project, and apply it.
3. Configure the selected module in Voxta.

Spotify has two explicit **Authorization Mode** choices. The existing **Temporary local listener (fallback)**
remains the default while the server-managed pipeline is tested.

To use **Spotify OAuth** in the desktop app, using Voxta's existing web server:

1. Add `http://127.0.0.1:5384/oauth-native-bounce.html` to your Spotify Developer Dashboard's redirect URIs.
2. Select **Spotify OAuth** in the module. Set **Spotify OAuth Redirect URI** to that exact address,
   confirm the client credentials, and save the module settings. Existing browser callback settings are
   preserved on upgrade, so replace `/auth/modules/callback` with `/oauth-native-bounce.html` for desktop use.
3. Click **Sign in** in **Spotify Account**. Voxta opens Spotify in your external browser. After consent,
   its existing bounce page returns the code/state to the desktop app through `voxta://oauth-callback`.
   Allow your browser to open Voxta when prompted.
4. Run **Test**, then start a new chat and test playback/tools. This mode uses only Voxta's encrypted account
   token store; it never starts the temporary listener or uses its saved token file.

For sign-in from a web browser, configure and register `http://127.0.0.1:5384/auth/modules/callback` instead.
Start and finish in the same browser so Voxta retains its OAuth state. Both callback paths can remain registered
in Spotify, but the module setting must match the client used to start sign-in. New configurations default to
the desktop callback. The selected URI is used unchanged for both authorization and code exchange; mismatches
are rejected with the exact address requested by Voxta. HTTPS browser origins are supported; HTTP requires an
explicit loopback IP. Existing setting keys and the stored mode value are retained despite the UI label rename.

To repeat a fresh login, use **Sign out** in Spotify Account and authorize again. Voxta owns deletion of the
stored token and account field; this does not require an unexpired Spotify token. On the next provider request,
an active Spotify client notices the removed account and clears its cached client. A `Spotify OAuth sign-out
requested` log entry confirms the request reached the module. Live desktop button behavior still requires
acceptance testing; an unresponsive UI is not treated as proof that token deletion completed.

To return to the fallback, select **Temporary local listener (fallback)** and save. Its **Fallback Redirect URI** is:

```text
http://127.0.0.1:5385/extensions/spotify/oauth2/callback
```

Keep that exact URI registered in Spotify as well, with no trailing period. The fallback temporarily listens on
its own loopback port during authorization, on the server machine. It retains its existing saved token file and
three-minute authorization timeout. Native account sign-out does not erase fallback credentials. Both paths stay
available until native runtime testing is complete; there is no automatic switch between them on failure.

Select **Spotify Tools** for native tool calling. All tools default on and **Offer Tools In** defaults to
Assistant and Companion. The **Spotify Tools** cards provide individual switches and editable descriptions.
Saved selections/styles are retained; legacy action inference stays inactive while Spotify Tools is selected.

Voxta displays module load errors on the Custom Modules page. The project uses the `1.13.0-alpha.10` Modules SDK and
keeps its package lock file in source control so local and release builds resolve the same dependencies.

## Supported actions

- Pause, resume, and toggle playback.
- Play requested or random music and configured special playlists.
- Queue tracks and other Spotify content.
- Change volume and seek within the current track.
- Skip tracks and change shuffle or repeat mode.
- Save the current track, list or update playlists, list devices, and transfer playback.

## Releases

Releases are module-specific. The tag must be `<module id>/v<version>`, and the version must exactly match that
module's `module.json`.

For example, to release Spotify `0.1.1`:

```powershell
# First set "version": "0.1.1" in Voxta.Modules.Aios.Spotify/module.json, then commit and push it.
git tag Voxta.Modules.Aios.Spotify/v0.1.1
git push origin Voxta.Modules.Aios.Spotify/v0.1.1
```

A version suffix makes a prerelease, for example `Voxta.Modules.Aios.Spotify/v0.2.0-beta.1`; use qualified tags for all new releases.

The workflows here call Voxta's reusable ones ([voxta-module-actions](https://github.com/voxta-ai/voxta-module-actions)).
`release.yml` builds the module, attests it, and creates the GitHub release with two assets:
`<id>-<version>-any.zip` (what Voxta installs) and `voxta-module.json` (the built metadata and zip SHA-256).
To create the same files locally for one module:

```powershell
dotnet publish Voxta.Modules.Aios.Spotify/Voxta.Modules.Aios.Spotify.csproj `
  -c Release -p:VoxtaModulePack=dist/spotify
```

A zip is at most 32 MB; download CUDA, models, and other large dependencies at runtime.

Users add `aios-ai/voxta-modules` in **Add Modules → Module Registries** and install from there. Voxta checks
each download against the sha256 in `voxta-module.json` and GitHub's own hash of the asset.

When a later Voxta breaks a release, cap its range without rebuilding: **Actions → edit release**, e.g.
`>=1.13.0, <1.15.0`. The same workflow yanks a release.

## Adding another module

1. Give it a unique project directory and module ID.
2. Add its project, `module.json`, assets, and committed `packages.lock.json`.
3. Add the project to `Voxta.Modules.slnx` for local all-module builds.
4. Push a branch or pull request; the build workflow discovers every tracked `module.json` and packages each module.
5. Release it with `<module id>/v<version>` after its manifest version is committed to `main`.

The **bump voxta** workflow intentionally updates the SDK, Voxta compatibility range, lockfile, and selected semantic
version component for every module in this repository together.

### In Voxta's own source

A repository Voxta trusts also publishes to Voxta's module source, listed in every Voxta without adding the repository:

1. Ask Voxta to trust it (they add it under **Admin → Module repos** in the Voxta portal, with its module ids).
2. Protect its release tags: Settings → Rules → Rulesets, with a tag rule targeting `*/v*`.
3. Set the repository variable `VOXTA_PUBLISH` to `true` (`VOXTA_API_URL` picks another Voxta Cloud, e.g. staging).

Releases then publish under the job's GitHub token, which tells Voxta Cloud the repository, commit, tag and workflow:
no key to store. The repository has to be public, unless Voxta owns it. Deleting a release does not remove it from
Voxta's source: yank it.

## Verify a release

In a public repository every zip is attested by GitHub: it was built by this repository's `release.yml` from the
tagged commit.

```
gh attestation verify Voxta.Modules.Aios.Spotify-0.1.0-any.zip --repo aios-ai/voxta-modules
```

The build is pinned (`packages.lock.json`, deterministic compiler output): rebuild the tag with
`dotnet publish -c Release -p:ContinuousIntegrationBuild=true` on the .NET SDK `voxta-module.json` names (`dotnetSdk`)
and the DLL matches the one in the zip. The zip itself differs (file times).

## License

MIT.
