# Voxta modules by aios

Installable Voxta modules maintained by aios. Add `aios-ai/voxta-modules` under
**Settings → AI Setup → Module Sources** (or **Add Modules → Module Registries**) in Voxta Server to discover published modules.

Each module has its own project directory, `module.json`, version, dependencies, and release history. A push to
`main` builds every module; a module-qualified version tag builds and releases only the named module.

All modules in this repository are free and require Voxta `1.13.0-beta.1` or newer.

## Modules

### Keyboard Shortcuts (`Voxta.Modules.KeyboardShortcuts`)

Lets a character trigger Windows keyboard shortcuts you explicitly configure on the local desktop.

- **How it works:** Define shortcuts in module settings as **Shortcut groups** (each enabled group appears as one chat tool) or **Standalone shortcuts** (each appears as its own chat tool). Open the **Shortcut Groups** link in module settings to browse saved shortcuts by group and export or import/merge group JSON files.
- **What to pay attention to:**
  - **Windows (`win-x64`) only**, admin-only install/settings, and opt-in per character (**Augmentations → Keyboard Shortcuts**).
  - Only runs in local sessions (disabled in remote or unattended sessions, or when computer session access is disabled).
  - You can optionally restrict any shortcut to a specific foreground `.exe` from Task Manager, or leave it blank for global shortcuts.
  - Shortcuts are marked as destructive tools so Voxta asks for approval. Reopen an active chat after enabling the module or changing shortcut bindings.

### OpenWeather (`Voxta.Modules.OpenWeather`)

Adds current weather, forecasts, air quality (current, forecast, and history), local astronomical sunrise/sunset times, and generated weather maps to Voxta chats via 5 native tools (`get_weather`, `get_air_pollution`, `get_air_pollution_history`, `get_sun_times`, `get_weather_map`).

- **How it works:** Uses OpenWeather's standard free APIs (no One Call subscription required) plus local astronomical/timezone calculations for `get_sun_times`. Queries accept city/state/country names, `zip:10115,DE`, or `lat,lon`, falling back to **My Location** when omitted. Individual tools and descriptions can be customized via **OpenWeather Tools** cards, and explicit requests can override default weather/pollution detail fields per call.
- **What to pay attention to:**
  - Requires an OpenWeather API key (rate-limited free plan available; stored encrypted in Voxta) and a tool-capable model.
  - Weather forecasts cover ~5 days in 3-hour steps; air pollution forecasts cover ~4 days in hourly steps; pollution history supports up to 31 days per call from 27 Nov 2020 onward.
  - Weather maps attach visibly to the chat and are passed to vision-capable models with bounds, units, and a color-legend reference. Note that Voxta queues chat image attachments behind the active reply/speech.

### Philips Hue (`Voxta.Modules.PhilipsHue`)

Controls Philips Hue lights, smart plugs, rooms, zones, scenes, color, and brightness from Voxta chats via 6 native tools, and can optionally expose a character-controlled light for emotion-driven changes.

- **How it works:** Connects directly to your Philips Hue Bridge on the local network. On chat startup, it snapshots your exact light, smart-plug, room, zone, and group names so the model can target them accurately, while scenes are queried through the inventory tool.
- **What to pay attention to:**
  - Requires a Philips Hue Bridge reachable on the local network. Initial pairing prompts you to press the physical bridge link button; credentials are stored encrypted in Voxta (use **Re-authenticate** in module settings to clear and redo pairing).
  - Inventory and connection status are initialized when a chat starts—restart the chat after adding/renaming lights or to retry a disconnected bridge.
  - Toggling all lights without a target excludes smart plugs. The emotion tool is disabled by default and requires setting **Character Controlled Light**.

### Spotify Control (`Voxta.Modules.Spotify`)

Controls Spotify playback, search, queueing, volume, seeking, shuffle, repeat, Liked Songs, playlists, and devices through 16 native tools, with automatic **Speech Ducking** while speaking or transcribing.

- **How it works:** Uses Voxta's built-in OAuth flow and a lightweight background monitor to track the active player, current track, and volume. Search returns numbered, human-readable selections bound to Spotify URIs for the current chat. **Speech Ducking Mode** supports either a fixed volume ceiling or a relative percentage reduction, restoring volume automatically after speech/transcription.
- **What to pay attention to:**
  - Requires a **Spotify Premium** account and your own app in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/) with **both** redirect URIs registered:
    - `http://127.0.0.1:5384/oauth-native-bounce.html` — desktop app
    - `http://127.0.0.1:5384/auth/modules/callback` — web browser
    *(For a different server address, register both paths with that origin using HTTPS or an explicit loopback IP such as `127.0.0.1` or `[::1]`, not `localhost`.)*
  - Enter **Client ID** and **Client Secret**, save, and click **Sign in** in **Spotify Account** (use **Sign out** or **Re-authenticate** to reset). Finish a pending sign-in within 5 minutes before switching clients.
  - Playback tools appear automatically only when an active Spotify player (playing or paused) is detected; without one, start playback manually on your device first.
  - Device discovery and transfer tools are disabled by default; enable or customize individual tools under **Spotify Tools** cards.

### Translator (`Voxta.Modules.Translator`)

Translates your messages into the character's configured language and translates the character's replies into your chosen **Reply language** locally using Mozilla's Bergamot engine.

- **How it works:** Runs on your Voxta server—message text is translated locally and is never sent to an external translation service. Direct translation models are preferred, falling back to an English pivot when no direct model exists.
- **What to pay attention to:**
  - **Windows (`win-x64`) only.** Requires an internet connection on first use of a language pair to download Mozilla translation models into Voxta's module data folder (cached and checksum-verified for later chats).
  - By default, spoken replies are translated into **Reply language** while written chat/history keeps the character's original wording. Turn on **Also translate replies in chat and history** if you read with speech off, and start a new chat after changing it.
  - Choose a voice that supports your **Reply language** (or set **Reply language** to the character's language to hear original replies while still typing/speaking in your own language).

## Develop

1. Run `dotnet build Voxta.Modules.slnx` to build the repository, or build a module project directly.
2. In Voxta, open **Add Modules → Add Custom Module**, select the module project, and apply it.
3. Configure the selected module in Voxta.

Voxta displays module load errors on the Custom Modules page. Each module targets `net11.0`, references the `1.13.0-beta.1` Modules SDK, and keeps its `packages.lock.json` in source control so local and release builds resolve the same dependencies.

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
dotnet publish Voxta.Modules.Spotify/Voxta.Modules.Spotify.csproj `
  -c Release -p:VoxtaModulePack=dist/spotify
```

A zip is at most 32 MB; download CUDA, models, and other large dependencies at runtime.

Users add `aios-ai/voxta-modules` in **Settings → AI Setup → Module Sources** and install from there. Voxta checks
each download against the sha256 in `voxta-module.json` and GitHub's own hash of the asset.

When a later Voxta breaks a release, cap its range without rebuilding: **Actions → edit release**, e.g.
`>=1.13.0, <1.15.0`. The same workflow yanks a release.

## Adding another module

1. Give it a unique project directory and module ID (`Voxta.Modules.<Name>`).
2. Add its project, `module.json`, assets, and committed `packages.lock.json`.
3. Add the project to `Voxta.Modules.slnx` for local all-module builds.
4. Add a concise section describing how it works and what to pay attention to in this README.
5. Push a branch or pull request; the build workflow discovers every tracked `module.json` and packages each module.
6. Release it with `<module id>/v<version>` after its manifest version is committed to `main`.

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

```powershell
gh attestation verify Voxta.Modules.Spotify-0.1.10-any.zip --repo aios-ai/voxta-modules
```

The build is pinned (`packages.lock.json`, deterministic compiler output): rebuild the tag with
`dotnet publish -c Release -p:ContinuousIntegrationBuild=true` on the .NET SDK `voxta-module.json` names (`dotnetSdk`)
and the DLL matches the one in the zip. The zip itself differs (file times).

## License

MIT.
