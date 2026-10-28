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
chats. Select **OpenWeather** to use its six native tools with a tool-capable model. The augmentation supports
configurable locations, units and reported details, without an action-inference service. Legacy actions and their
settings have been removed after user acceptance. Existing chats using only the old action augmentation must
select OpenWeather; existing tool-enabled chats keep their selection.

All six tools default enabled in Assistant and Companion chats. The **OpenWeather Tools** cards let you disable
individual tools and edit their descriptions. City queries use **My Location** when no location is supplied;
without either, the agent can ask which city to use. Maps support countries, continents and global views and
attach images visibly to the chat and return them to the model with geographic bounds, layer units and a local
color-legend reference for brief visual descriptions. This adds no extra API requests.
Weather and pollution detail selections are defaults for general questions. Explicit requests can select unchecked
fields through the tools' optional `details` argument (a comma-separated field list), without changing settings.
Weather includes humidity, pressure, gusts, sunrise/sunset and data time; forecasts also expose precipitation
probability. Sun times include the destination offset; forecast sun times are single-date city metadata.
`get_air_pollution_history` accepts ISO 8601 `start`/`end` timestamps with a UTC offset or Z, from 27 November
2020 onward, up to 31 days per call. The end is exclusive; history and pollution forecasts summarize UTC
periods. Weather forecasts use destination day/night periods. Locations accept full city/state/country names,
`zip:10115,DE` (country required), or `52.52,13.405` (latitude,longitude). These features use the standard
free APIs without a One Call subscription.
The API key stays in Voxta's encrypted password-field configuration.

The module itself is free. It requires Voxta `1.13.0` or newer and an OpenWeather API key; OpenWeather offers a
rate-limited free plan.

### Spotify Control

`Voxta.Modules.Aios.Spotify` adds Spotify playback control to Voxta chats. It connects through Spotify OAuth and
exposes 17 native tools for playback, search, queueing, volume, seeking, shuffle, repeat, favorites, playlists, and devices.

The module itself is free. It requires Voxta `1.13.0` or newer, a Spotify Premium account, and a Spotify Developer
application that provides a Client ID and Client Secret.

## Develop

1. Run `dotnet build Voxta.Modules.slnx` to build the repository, or build a module project directly.
2. In Voxta, open **Add Modules → Add Custom Module**, select the module project, and apply it.
3. Configure the selected module in Voxta.

Spotify uses Voxta's supported module OAuth flow for both the desktop app and web browser. No additional
HTTP server is started by the module.

1. Create or open your application in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).
2. Add **both** redirect URIs to your Spotify application's settings:
   - `http://127.0.0.1:5384/oauth-native-bounce.html` � desktop app
   - `http://127.0.0.1:5384/auth/modules/callback` � web browser
3. Enter the application's **Client ID** and **Client Secret** in the module, save, and click **Sign in** in
   **Spotify Account**. Voxta supplies the correct callback automatically for the client that starts sign-in.
4. Complete Spotify authorization. For desktop sign-in, allow the browser to return to Voxta when prompted.
   For browser sign-in, finish in the same browser so Voxta retains its OAuth state.
5. Run **Test**, then start a new chat and test playback/tools.

You can sign in from desktop or browser without changing module settings. The callback chosen by Voxta is
retained for that sign-in and used unchanged during token exchange. The module does not try alternate URLs
on failure. Finish an active sign-in before starting one for the same module from a different client; an
unfinished sign-in expires after five minutes. If Voxta restarts during sign-in, start a new sign-in.

For a different Voxta server address, register both callback paths with that origin in Spotify. Remote
addresses require HTTPS; local HTTP requires an explicit loopback IP such as `127.0.0.1` or `[::1]`, not
`localhost`. You still do not enter or select redirect URLs in the module.

**Sign out** clears the stored account through Voxta, including when the Spotify token has expired. On its
next provider request, an active client notices the removed token and clears its cached connection. The module
logs `Spotify OAuth sign-out requested` when the request reaches it.

Select **Spotify Control** for native tool calling. All tools default on and **Offer Tools In** defaults to
Assistant and Companion. The **Spotify Tools** cards provide individual switches and editable descriptions.
Saved selections/styles are retained. Spotify's legacy actions, wake word, connect gate, and connection flags
have been removed. Chats that used only **Spotify Actions (Legacy)** must select **Spotify Control**.

All Spotify tools appear automatically when an active player is detected, including a paused player.
Without one, all Spotify tools are hidden. Context guides the assistant to ask the user to open Spotify on
their preferred device and start playback manually, then continue their original request. No status or connect
tool is needed: the background monitor restores availability automatically. Changes apply to
subsequent assistant generations, normally within the monitor's one-second polling interval. A tool you disabled
in settings stays disabled. Playback context tells the assistant when manual startup or authorization is needed.

**Spotify Status** combines player availability and playing/paused state in one context. Saving a track to
Liked Songs reports when it is already saved, without adding it again.

**Speech Ducking Mode** offers a fixed volume limit or a percentage reduction from the normal volume.
Existing settings keep the fixed limit. For a normal volume of 60%, choose percentage reduction with 25% to
duck to 45%, or about 17% to duck to 50%. Fixed mode never raises a quieter player. Both modes restore the
normal volume after speech/transcription, including volume changes requested while ducked.
Set the fixed limit to 100 or the relative reduction to 0 to disable ducking.

Tokens are stored through Voxta's internal account store. The module creates no local token directory or files;
old token files are ignored.

Voxta displays module load errors on the Custom Modules page. The project uses the `1.13.0-alpha.10` Modules SDK and
keeps its package lock file in source control so local and release builds resolve the same dependencies.

## Spotify tools

- Pause, resume, and toggle playback.
- Play requested or random music and configured special playlists.
- Search Spotify and queue selected tracks.
- Change volume and seek within the current track.
- Skip tracks and change shuffle or repeat mode.
- Save the current track, list or update playlists, list devices, and transfer playback.

## Releases

Releases are module-specific. The tag must be `<module id>/v<version>`, and the version must exactly match that
module's `module.json`.

After updating module versions, run the locked Release build, commit, push `main`, then publish with:

```powershell
./scripts/publish-module-releases.ps1 -Publish
```

The script creates qualified tags for untagged module versions and pushes them atomically. A version suffix
makes a prerelease. Monitor the release workflow and verify both install assets before reporting completion.

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
