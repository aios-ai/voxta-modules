# Tools foundation and migration contract

## Current boundary

OpenWeather continues to use `IActionInferenceAugmentation`. Hue retains its legacy actions and adds the opt-in
`hue_tools` augmentation for comparison. Spotify exposes only the opt-in
`spotify_tools` augmentation and uses the shared catalog and native agent loop. Its legacy actions, wake word,
connection/playing flags, connect alias, automatic search ranking, and action feedback have been removed after
user verification of tools. Existing chats configured only for the removed `spotify` augmentation must select
**Spotify Tools**. Obsolete action settings are ignored; saved tool choices and styles remain compatible.

The shared foundation is compiled privately into all three modules. Spotify and Hue register tools; OpenWeather remains actions-only.

## Philips Hue integration

- `hue` retains the existing action inference integration. `hue_tools` is a separate opt-in plan,
  excluded from catch-all activation, using the SDK tools-service marker and shared catalog/adapter.
  When both are selected, legacy activation returns no instance so pairing, notes and commands cannot duplicate.
- `Tools/HueToolCatalog.cs` defines eight native tools: on/off, bridge connection, color, brightness,
  scene activation, inventory and emotion. Definitions and handlers are independent of action schemas,
  match rules, flags and feedback. Tool results return directly into the agent loop. Tools-only sessions
  send no inventory notes or legacy flags; inventory is available through `hue_show_hue_inventory`.
- `HueToolsSettings` owns separate editable description cards and disable switches, with Assistant/Companion
  defaults. All tools except emotion default enabled. Emotion additionally requires `CharacterControlledLight`;
  it executes during the agent loop rather than after the assistant message. Action settings are not consulted.
- `HueToolHandler` serializes calls per session and uses the existing neutral `HueManager`/services.
  Exact unique names resolve against inventory; missing or ambiguous targets return an error without a command.
  Only omitted on/off targets mean all lights (excluding smart plugs). Brightness is an integer from 1 to 100.
  Scene resolution accepts native room/zone ownership or grouped-light ownership and sends the scene resource ID.
  Mutations use Act permission, inventory uses Read, and bridge-supplied names are external content.
  Results distinguish bridge acceptance from physical verification and retain partial all-light failure warnings.
- Local Hue bridge link-button pairing and per-user app-key storage remain setup concerns; no OAuth provider,
  callback route or cloud account is needed. Tools use the existing SDK user-interaction request for pairing.
  Connection and commands use the saved local bridge configuration. No command is automatically retried.
  HueApi command methods expose no transport cancellation parameter: cancellation is checked before dispatch,
  between all-light commands and after inventory initialization; in-flight commands are awaited under the session gate.

Run `dotnet run --project tests/Hue.Tools.Tests -c Release -p:RestoreLockedMode=true` for fake-service
coverage of all operations, validation, ambiguous targets, scene ownership, failure/no retry, cancellation,
permissions, disabled tools, emotion gating and independent SDK plans. Live Voxta testing must compare
**Philips Hue Tools** alone against **Philips Hue** alone, including link-button setup, real lights and plugs,
scene selection, multiple calls, permission prompts and emotion behavior. Automated checks do not establish
live hardware parity.

## Spotify integration

- `Tools/SpotifyToolCatalog.cs` owns 16 native definitions and SDK schemas, permissions, choices, and bindings.
  `SpotifyToolHandler` validates and executes through `ISpotifyManager`; it never
  references the action handler or sends notes, cancels replies, or triggers generation. Mutating calls are
  serialized within the session and return immediate tool results; command summaries are not persisted in context.
- `spotify_search` is a read-only catalog query. It returns up to ten candidates of one requested type in
  Spotify order with identifying metadata and a human-readable `Selection`; it never selects or starts music.
  Track results include their album edition's release date; album and episode results also carry their dates.
  `ReleaseDatePrecision` preserves Spotify's year/month/day precision without inventing missing components.
  A track's album date is not necessarily the song's original release date. Artists, playlists and shows have
  no fabricated release dates. The agent passes the exact `Selection` to `spotify_play_music` or, for a track,
  `spotify_queue_track`. The host's approval displays this argument, including title, artist, album and date
  where available. `SpotifyToolSelections` privately binds each selection to one URI within the chat, retaining
  up to 200 items. Duplicate titles receive distinct numbers; retained results reuse their selection and
  expired labels are never rebound. Unknown/expired selections fail before any playback command and require
  another search. Search results omit internal URIs. A raw URI remains accepted in `selection` when supplied
  directly by the user. Separate `uri` and name/type arguments are no longer supported. Artist selections ask Spotify to select
  music in that artist's context; a specific song requires selecting a track result. Genre requests use
  playlist searches or Spotify's genre filter for tracks. There is no background candidate ranking or automatic selection.
- `ISpotifyManager.PlayWithResult` distinguishes command rejection, accepted-but-unconfirmed playback, and
  observed playback. Tools return these states plus the observed item/device rather than marking an
  acknowledged command as failed. Polling is bounded and never repeats the play mutation. Artist playback
  can be confirmed by the playing track's artist URI when context is missing or different. Exact track/episode
  requests still require the requested item; paused or unrelated playback is not confirmation.
- `SpotifyToolsSettings` exposes `SpotifyTools` through the same `FormArrayOfObjectsField` card editor used
  by actions, with fixed names, per-tool disable switches, and editable descriptions. It reads existing nonempty
  SDK `Tools` selections as a migration fallback until cards are saved; an empty beta.1 selection adopts the new
  current defaults (device discovery and transfer off, other tools on). Saved card switches take precedence.
  Card parsing accepts property names without case sensitivity and both string and boolean disable switches. `ToolChatStyles` retains the SDK setting key and
  choices, with module-local Assistant and Companion defaults; explicitly saved styles remain intact.
  Obsolete `Actions` settings are ignored. `SpotifyChatAugmentationsService` implements the
  SDK tools-service marker and plans tools separately as opt-in, excluded from catch-all activation.
- `spotify_skip` requires `direction` (`next` or `previous`) and replaces both directional skip tools.
  Old card settings migrate conservatively: disabling either old direction disables the combined tool;
  an explicit combined-tool card takes precedence. An old explicit `Tools` selection enables the combined
  tool only if both directions were selected. Direction-specific description overrides are not copied.
- Shuffle commands are sent once, then confirmed by up to five playback-state reads at 400 ms intervals.
  Success requires an active device and the requested shuffle state. Accepted but unchanged or missing-player
  observations return explicit unconfirmed feedback. Tools must not claim success, retry automatically, or
  start different music to enable shuffle.
  Liked Songs resolves the current track automatically and checks `/me/library/contains` through the SDK first.
  An already saved track returns `AlreadySaved` without a write; a failed or malformed membership check stops
  before saving. The handler distinguishes already present, newly saved, and failed outcomes.
  Its library save uses the authenticated SDK connector
  with `uris` in the query string and no body, matching Spotify's `/me/library` contract; SDK 7.4.2's
  `Library.SaveItems` incorrectly places `uris` in the body. Revisit this workaround when upgrading the SDK.
- `SpotifyToolsSession` owns one `SpotifyPlaybackSession`, which starts/stops the playback monitor and
  forwards speech/transcription events to `SpotifySpeechDucking`. Ducking preserves desired volume across
  overlapping events and tool volume changes. Fixed mode retains the saved `SpeechDuckingVolumePercent` ceiling
  and remains the default. Relative mode uses `SpeechDuckingReductionPercent` (default 25): 60 becomes 45,
  rounded to the nearest integer. Both modes only lower volume and restore the desired unducked volume once
  overlapping events finish. A volume tool call during ducking updates that desired volume before applying
  the selected reduction. Fixed 100 or relative 0 disables ducking. Session disposal stops monitoring before
  releasing its cancellation source.
- Voxta asks `IToolsChatAugmentation.GetToolsAsync` for definitions per generation and filters disabled tools.
  The session combines saved switches with the monitored active-player state: selected tools
  are offered only with an active device, including paused playback. Without a player, context asks the assistant
  to guide manual startup on the user's preferred device and continue the original request once available.
  No status/connect tool is registered; the background monitor restores availability. All definitions remain discoverable; a
  state change never enables a saved disabled tool or changes permissions. There is no connect gate or flag.
  The one-second monitor makes changes visible on a subsequent generation; this does not replace a running
  generation's tool list. Calls already offered are validated against fresh provider state by their handlers.
  One `Spotify Status` context reports authorization, errors, manual startup, or playing/paused state.
  Device, volume, and current-track metadata remain separate contexts.
- OAuth remains a setup/session concern. Search/profile requests receive both call and session cancellation.
  Fresh provider playback state is used for commands that depend on current playback; passive contexts update
  through the monitor. SDK pagination has no transport cancellation parameter; cancellation stops waiting for
  subsequent read-only playlist pages.

In Voxta, review **Spotify Tools** cards and **Offer Tools In**, then select **Spotify Tools** for a new chat
with a tool-capable model. Check playback/search, playlists, queue, seek/volume, library changes, transfer,
multiple calls, cancellation, and speech ducking. Without an active player, verify no Spotify tools are offered
and context provides manual-start guidance without requiring a tool call;
start playback manually and verify playback tools appear on the next generation, including after pausing.
Saved disabled tools must stay disabled. Results should appear in the current reply without extra notes or
another generation. Runtime/provider behavior and automatic visibility remain manual acceptance steps.

## Spotify authorization boundary

### Why the original route stopped working

The original module used `AddControllers().PartManager.ApplicationParts.Add(...)` and an anonymous
`SpotifyController` at `/api/extensions/spotify/oauth2/callback`. It already exchanged the received code for
access/refresh tokens. This was a real pipeline through Voxta's existing web server.

Comparison of the locally available server builds established the registration change:

| Server build | Owning implementation | Effect on the original module |
| --- | --- | --- |
| 1.12.0 + `165a82d` | `Microsoft.Extensions.DependencyInjection.VoxtaModuleBuilder(IServiceCollection services, ...)` retains the host collection; the host initializes it with its own `services`. | Module-added MVC application parts can be seen by the host's `MapControllers`. |
| 1.13.0 + `34ff5a1` and + `a0c46bd` | `Voxta.Core.Modules.ModuleBuilder.Services` returns `LoadedModule.Services`, a separate collection. `LoadedModule` builds a module-owned container. | The same MVC registration remains private to the module and no longer exposes a host route. |

The exact upstream change commit has not been identified; these are inspected build snapshots. No old server
was launched or installation modified during this comparison. Module commit `601615f` also moved the configured
redirect from `/api/extensions/...` to `/extensions/...`; that path did not establish a host route. The current
`ModuleEndpointsController` dispatches `IModuleHttpEndpoints` under `/api/extensions/{prefix}/...`, with Voxta
login required. It cannot replace the old anonymous controller simply by restoring that controller's attributes.
Spotify still accepts HTTP loopback IP redirect URIs and authorization-code flow. The reported failure already
contained an authorization code; the receiving route was the failure, rather than Spotify rejecting the login.

### One Spotify OAuth flow with automatic callback selection

`SpotifyModuleAuthorizationProvider` implements the SDK's `IOAuth2ModuleAuthorizationProvider`, registered by
`AuthProviderType`. `Spotify Account` is a native `FormAuthorizeField`. Users enter Client ID and Client Secret,
register both callback URLs shown in the documentation field in Spotify, save, and sign in. There is no auth-mode
selector, redirect setting, token-file path, or module-owned HTTP listener.

Voxta owns the account UI, OAuth state validation, callback handling, `ModulesAuthController`, and encrypted
token persistence. The module owns the Spotify consent URL, scopes, token exchange, and refresh adapter.
`GetOAuth2AuthorizationUrlAsync` receives the actual `returnUrl` selected by the initiating client:

| Client starting sign-in | Callback URI for the local server |
| --- | --- |
| Desktop app | `http://127.0.0.1:5384/oauth-native-bounce.html` |
| Web browser | `http://127.0.0.1:5384/auth/modules/callback` |

The desktop frontend opens authorization externally; the host's existing bounce page normalizes code/state into
`voxta://oauth-callback`. `Voxta.DesktopApp.MainWindow.HandleOAuthCallback` navigates its WebView to the local
`/auth/modules/callback` page, where the original OAuth state is available and the native token exchange runs.
The module adds no routes or deep-link handlers. For browser sign-in, finish in the initiating browser.

Both SDK alpha.10 and alpha.11 supply `returnUrl` on authorization start, but do not supply it (or state) to
`ExchangeCodeForTokenAsync`. `SpotifyOAuthFlows` is a module-owned singleton that bridges these two provider calls.
The provider uses the authenticated user ID and the existing module auth endpoint's `moduleId` route value from
the host's standard `IHttpContextAccessor` to isolate each pending flow. That accessor is already registered by
Voxta; no host configuration or routing is modified. The callback must use the current request's origin, one
of the two supported paths, HTTPS or HTTP on an explicit loopback IP, and no query/fragment/user information.

Pending records contain only the selected URI, a credentials fingerprint, and a five-minute expiry. They are
consumed once before code exchange, cleared on sign-out, and expired records are pruned on subsequent sign-in.
The fingerprint rejects credential changes between consent and exchange. Another module or user cannot consume
the record. Same-client restarts retain an unambiguous URI; a pending sign-in from a different client for the
same module must finish or expire before switching clients. Without state in the exchange contract, accepting
both at once could pair a code with the wrong URI. A server restart during consent requires restarting sign-in.
No alternate URI is guessed or retried on failure. Voxta continues to own browser state/nonce validation.

`SpotifyVoxtaTokenStore` adapts only `IModuleOAuthTokenStore`: decrypt reads and encrypt refresh updates, scoped
by the current user and saved `SpotifyAccount` token reference. `SpotifyManager` uses that store for every
provider request, serializes refresh within each manager to avoid duplicate rotations, and recreates the API client for a changed
access token. Missing or rejected credentials produce account-field instructions; they never start another
browser flow. `ModulesAuthController.SignOut` owns token deletion and clearing the account field. The provider
also cancels pending consent and logs a diagnostic. The manager drops its cached client on observing removal.

The listener, callback manager, manual redirect/mode fields, token-file helpers, and interaction-request wrappers
have been removed following user acceptance of native authorization. Existing `SpotifyAccount` references remain
compatible. Obsolete persisted settings are ignored. Old local token files are neither read nor deleted; they
are no longer part of the module's authorization pipeline.

## SDK contract

The owning API is the installed `Voxta.Sdk.Modules` / `Voxta.Abstractions` **1.13.0-alpha.10** package,
which is already the manifest compatibility floor. The foundation compiles against that version:

- `IToolsChatAugmentation` provides `GetToolsAsync` and `CallToolAsync` without requiring action inference.
- `IToolsChatAugmentationsService` is the SDK's tools-capable service marker, to apply when integrating tools.
- `ChatTool` owns name, description, JSON input schema, source, augmentation key, enabled state, chat styles,
  permission class, display mode, speech synchronization, and external-content metadata.
- `ChatToolSchema.FromArguments` builds schemas from SDK `FunctionArgumentDefinition` objects. Tool argument
  definitions must live in tool files; do not obtain them by calling the old action builders.
- `ChatToolSettingsContributions` supplies standard tool selection and chat-style settings. Spotify uses the
  SDK choices and settings compatibility but presents per-tool configuration in the SDK's object card editor,
  as requested by the user. Its module-local style field does not mutate SDK static defaults. Tool settings must
  not read legacy `Actions` settings.
- `ChatToolCallResult` returns text, errors, images, and other SDK result metadata directly to the agent loop.

The SDK and host own tool permissions, grants, model visibility, and the agent loop. The catalog does not
provide permission authorization. In particular, `Enabled` checks are an availability guard, not a substitute
for the host permission gate. Persistent permission grants are not implemented by the module. The module does not customize the host's
approval rendering; play/queue expose readable selection arguments and leave permission decisions to the host.

Voxta's [Assistant Tools documentation](https://doc.voxta.ai/docs/server/services/chat-augmentations/basic-tools)
also describes tools running during a reply and requiring a model that supports tool calling.

## Shared implementation

`Shared/Tools/Tools.projitems` is imported by each module project. Its internal classes are compiled privately
into each module, avoiding an additional shared DLL, installation dependency, or installable module manifest.
The test executable imports the same source.

| Component | Responsibility |
| --- | --- |
| `ModuleTool` | Pair one native `ChatTool` with one session-bound execution delegate. |
| `ModuleToolCatalog` | Reject duplicate names; snapshot discovery metadata; dispatch exact names; reject disabled/unknown tools and non-object arguments; preserve native results and cancellation; log unexpected failures and return a safe error. |
| `ModuleToolInputException` | Return a deliberately safe, actionable argument validation error to the model. |
| `ModuleToolsAugmentation` | Adapt a catalog to `IToolsChatAugmentation`, with no action-inference service dependency. |

Construct one catalog per session with handlers bound to that session's clients and domain settings. Definitions
are a snapshot; rebuild the instance when settings change. Disabled definitions stay discoverable for the host's
settings UI but cannot execute. Names use ordinal matching. Every tool must have a nonempty source and description.
Use module-prefixed names (`openweather_get_weather`, `spotify_play_music`, `hue_change_color`) so tools from
multiple modules cannot shadow one another. Set `Source` to the module's `VoxtaModule.ServiceName` and set the
tool-specific `AugmentationKey` explicitly at integration time.

The catalog checks only the JSON envelope, not arbitrary JSON Schema constraints. Each handler must validate
required fields, types, enum values, and ranges before invoking a client. Return expected provider/domain failures
as `ChatToolCallResult(IsError: true)`; throw `ModuleToolInputException` for safe input guidance. Pass cancellation
through all calls and never retry side effects automatically. Avoid including secrets in error text or logs.

The adapter does not own or dispose clients and monitors. The module's session owner must dispose them exactly
once and stop background work before disposing their dependencies. Mutable handler state needs its own synchronization;
the catalog does not serialize independent calls or assume the host always calls sequentially.

## Repeated integration pattern

For each module, add these module-owned files alongside the shared foundation:

```text
Tools/<Module>ToolCatalog.cs       native definitions, SDK schemas, settings choices, handler bindings
Tools/<Module>ToolHandler.cs       validation and execution, returning ChatToolCallResult
Tools/<Module>ToolsSettings.cs     tool-specific settings, independent of legacy action configuration
```

For future OpenWeather migrations and the Hue comparison period, keep action files and legacy registrations until user acceptance; suppress activation when tools take precedence. Tools must never call `TryHandleActionInference`,
an action handler, or construct synthetic `ServerActionMessage` objects. Actions must never dispatch through
the tool catalog. Both may call transport-neutral domain clients/services. If a useful operation currently lives
inside an action handler, extract a domain operation returning data/results, then give actions and tools their own
presentation adapters. Do not share action-specific note/reply plumbing with tools.

During integration, expose a separate opt-in tools augmentation (`<existing augmentation key>_tools`) and keep the
existing action augmentation key for compatibility. Implement the SDK tools-service marker on the service and plan
the tool instance independently. Selecting tools alone must not create an action instance or require ActionInference.
During future migrations, users can select tools alone or legacy actions alone for comparison. Do not automatically select both, which
could cause duplicate device commands. Spotify tools default enabled except device discovery and transfer, which require explicit opt-in; the
augmentation itself remains opt-in. Other migrations should choose defaults explicitly and version the change.

Use `Read` for queries and `Act` for changes to playback, libraries, or lights. Do not force `AlwaysAllowed`.
Mark results containing third-party text with the SDK's external-content metadata as appropriate. Describe results
as facts/data for the model, without instructions to trigger another reply. Tool execution must not call action
feedback helpers, `SendNoteAsync`, `SendNoteAttachmentAsync`, cancel a reply, or trigger another generation to report
its result. Return map images through `ChatToolCallResult.Images` instead.

## Migration coverage

Spotify migration is complete. For OpenWeather and Hue, preserve legacy actions until each tools migration is accepted.
Prefix tool names as described above.

| Module | Operations to cover | Migration-specific behavior |
| --- | --- | --- |
| OpenWeather (5) | `get_weather`, `get_weather_forecast`, `get_air_pollution`, `get_air_pollution_forecast`, `get_weather_map` | Reuse `IOpenWeatherClient` and summarizers. Preserve configured location fallback, units, selected details, and cache path. Separate data/formatting from note feedback. Return maps as images plus descriptive text. All five are queries. |
| Hue (8) | `turn_lights_on`, `turn_lights_off`, `hueBridge_connect`, `change_color`, `show_emotion`, `change_brightness`, `activate_scene`, `show_hue_inventory` | Reuse Hue manager/services. Validate brightness and target selection; expose useful disconnected/not-found errors. Inventory is Read; device/connection changes are Act. Keep emotion control gated by the configured character light and opt-in tool selection; agent-loop timing is distinct from the old after-assistant action timing. Preserve setup/link-button interaction separately from tool-result feedback. |
| Spotify (16, completed) | `toggle_playback`, `search`, `play_random_music`, `play_special_playlist`, `play_music`, `queue_track`, `volume`, `seek_playback`, `skip`, `repeat_mode`, `shuffle_mode`, `add_to_favorites`, `get_playlists`, `add_to_playlist`, `list_devices`, `transfer_to_device` | Native tools use neutral manager operations. Search and playlist/device listing are Read; mutations are Act. Configured playlists, playback context, and speech ducking remain independent of tool dispatch. OAuth and passive monitoring remain session/setup concerns. |

Spotify speech events use `SpotifyPlaybackSession` and `SpotifySpeechDucking`, independently of tool dispatch.
Spotify playback tools use the active player shown in context, including an active paused player. When Spotify
reports no active player, the assistant asks the user to open Spotify on the intended device, select a track or
playlist and start playback manually, then waits for confirmation that playback has started. Discovery and transfer
serve explicit user requests to change destination; they are not missing-player recovery steps. Shared behavioral guidance is published once as **Spotify Tool Guidance**, built by `SpotifyToolGuidance`
from saved enabled tools and included by `SpotifyPlaybackMonitor` only while a player is active. It covers
pause/resume, exact selections and user-supplied URIs, accepted versus confirmed results, no automatic mutation
retries, and explicit destination changes only where the respective tools are enabled. With search disabled,
play/queue guidance asks for a retained selection, user-supplied URI, or enabling search. Editable descriptions
contain selection/argument semantics; overrides do not remove shared policies. No-player startup guidance
remains in Spotify Status. Explicit no-active-player errors
produce manual-start guidance; generic 404 or device errors are not assumed to mean there is no active player.
The tools session awaits a read-only player-state preflight before activation completes. The existing
`SpotifyPlaybackMonitor` owns stable status, active-device, volume and track contexts, refreshed every second.
It does not discover devices at startup or in the background. No active player is distinct from a provider failure,
missing authorization, or an active paused player. Full context comparison detects device switches and track removal
without republishing unchanged ticks. Context contains current state only; tool results report command outcomes
without a persistent last-command context.
Seeking distinguishes relative seconds (`forward`/`backward`), relative percentages of the total duration
(`forward_percent`/`backward_percent`, measured from the current position), and absolute positions from the
beginning (`to_time`/`to_percent`). Percentage values are integers from 0 to 100; targets are clamped to the
track or episode boundaries. For example, at 3:00 in a ten-minute track,
forward by 10% reaches 4:00, whereas go to 10% reaches 1:00.
On a requested destination change, `spotify_list_devices` reports names, types, active status and control availability.
Transfer accepts device names and resolves current internal IDs privately. Neither discovery output nor playback
context exposes IDs, so approval arguments use names. Duplicate or ambiguous names require clarification or renaming
in Spotify. Restricted devices and devices without IDs are rejected. Transfer success means Spotify accepted the
transfer, not that playback is ready; it preserves the playing/paused state. No tool silently transfers or retries a
mutation.
`SpotifyManager.PlayWithResult` starts individual tracks through their album context with a track offset,
avoiding desktop clients that accept a bare `uris` request but clear the player. Album lookup must succeed
before changing playback; Spotify may continue with later album tracks after the selected song. After one
play command, up to five playback-state reads at 400 ms intervals confirm the requested track/episode or
context is playing. Acceptance without confirmation is reported explicitly as unconfirmed; no play command is retried.
Playback monitors and initial Hue inventory notes are session features; choose their owner explicitly when both
augmentations are selected to avoid duplicate polling/notes and double disposal.

## Validation and completion

Build all modules with locked packages:

```powershell
dotnet build Voxta.Modules.slnx -c Release -p:RestoreLockedMode=true -m:1
dotnet run --project tests/Tools.Tests -c Release -p:RestoreLockedMode=true
dotnet run --project tests/Spotify.Tools.Tests -c Release -p:RestoreLockedMode=true
```

`Tools.Tests` is a standalone executable: assertions fail the process and successful checks print their names.
It covers SDK dispatch, metadata ownership, disabled/unknown tools, invalid argument envelopes, safe failures,
cancellation, and no automatic retry. It does not test live providers or host registration. `Spotify.Tools.Tests` exercises the compiled Spotify
catalog/handler against a fake manager: all 16 operations, mutation failures, input validation, missing state,
ambiguous devices, cancellation, metadata/settings defaults, ducking overlap and volume changes, session dispatch,
playback-only preflight, manual-start recovery guidance, paused/playing states, private name-to-ID resolution,
stable context identities, device switches, provider failures and recovery,
tools-only registration, dynamic availability, saved disable switches, no flag writes, disposal, tool-card settings, description overrides, defaults/migration, redirect validation, and
single-flow authentication settings. `SpotifyVoxtaOAuthChecks` covers automatic browser/desktop callbacks,
exact consent/exchange URI matching across transient provider instances, request-origin/path validation,
user/module isolation, expiry, changed credentials, overlapping-client guards, sign-out, cancellation, no retry,
encrypted storage, missing tokens, serialized refresh, and invalid grants. `SpotifyPlaybackChecks` uses the real SDK
with a fake HTTP transport to cover shuffle confirmation and ignored commands, library-save query serialization,
album-context requests, resolved track URIs, delayed confirmation, empty or
wrong playback, lookup failures, playlist contexts and cancellation without replaying commands. These checks make no Spotify network
calls and do not replace live browser/desktop acceptance testing. The user verified native desktop sign-in before
listener removal; automatic client switching still requires runtime acceptance after this change.

For each migration, add handler checks with fake domain clients for all operations, invalid inputs, provider failure,
and cancellation. Then test in Voxta with tools selected and legacy actions deselected: discovery/settings, host
permissions, successful and failed calls, multiple calls in one reply, cancellation, and results in the same agent
response. Verify no ActionInference requirement, duplicate notes, follow-up generation, or repeated side effects.
For OpenWeather migration and Hue comparison, repeat representative requests with only legacy actions selected. Test real maps, Hue devices and emotion control,
Spotify playback and ducking against their providers before declaring behavioral parity.

Each externally visible migration increments the affected module's manifest version (use a prerelease while testing),
then follows `AGENTS.md`'s locked build, commit, push, publish, workflow, and release-asset verification flow. Update
the SDK reference and lockfile with the manifest range only if a newer API is required. Delete legacy files only
after the tools have been verified and the user authorizes removal.
