# Tools foundation and migration contract

## Current boundary

OpenWeather and Hue continue to use `IActionInferenceAugmentation`. Spotify now offers independent
`spotify_tools` and legacy `spotify` augmentations. Its tools use the shared catalog and native agent loop;
legacy actions retain their own configuration, argument builders, handler, and feedback. Selecting Spotify
Tools suppresses legacy instance creation even if both keys are selected, preventing duplicate commands,
monitoring, and speech event handlers. With tools deselected, legacy actions remain available for comparison.

The shared foundation is compiled privately into all three modules. Only Spotify currently registers tools.
Its migration is a beta for runtime testing; remove action inference code only after user verification and
authorization. Other modules' behavior and versions are unchanged.

## Spotify integration

- `Tools/SpotifyToolCatalog.cs` owns 17 native definitions and SDK schemas, permissions, choices, and bindings.
  `SpotifyToolHandler` validates and executes through `ISpotifyManager` and `SpotifySearchService`; it never
  references the action handler or sends notes, cancels replies, or triggers generation. Mutating calls are
  serialized within the session and recorded through the playback monitor's last-command context.
- `SpotifyToolsSettings` exposes `SpotifyTools` through the same `FormArrayOfObjectsField` card editor used
  by actions, with fixed names, per-tool disable switches, and editable descriptions. It reads existing nonempty
  SDK `Tools` selections as a migration fallback until cards are saved; an empty beta.1 selection adopts the new
  all-enabled defaults. Saved card switches take precedence. `ToolChatStyles` retains the SDK setting key and
  choices, with module-local Assistant and Companion defaults; explicitly saved styles remain intact.
  These settings are independent of legacy `Actions`. `SpotifyChatAugmentationsService` implements the
  SDK tools-service marker and plans tools separately as opt-in, excluded from catch-all activation.
- `SpotifyToolsSession` owns one `SpotifyPlaybackSession`. The legacy instance uses the same neutral playback
  owner when selected alone. That owner starts/stops one monitor and forwards speech/transcription events to
  `SpotifySpeechDucking`, which preserves the desired volume across overlapping events and tool volume changes.
  The action handler's volume-ducking methods remain compatibility wrappers; tools and script events do not
  call them. The manager is not disposable; the session stops monitoring before releasing its cancellation source.
- OAuth remains a setup/session concern. `spotify_connect` returns the existing no-active-client guidance;
  it does not invent a connection API. Search/profile requests receive both call and session cancellation.
  Fresh provider playback state is used for tool commands that depend on current playback; passive contexts
  continue to update through the monitor. The SDK's pagination helper has no transport cancellation parameter;
  cancellation stops waiting for subsequent read-only playlist pages.

To test the beta in Voxta, review the **Spotify Tools** cards and **Offer Tools In** settings, then select **Spotify Tools** for a new chat with a tool-capable model. Legacy Spotify Actions can be
deselected; selecting both still creates only the tools session. Check permissions, playback/search, playlists,
queue, seek/volume, library changes, device transfer, multiple calls in one reply, cancellation, and speech ducking.
Results should appear in the current reply without extra notes or another generation. For legacy comparison,
deselect Spotify Tools and select Spotify Actions (Legacy). Runtime/provider parity remains a manual acceptance step.

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
are no longer part of the module's authorization pipeline. Legacy action inference remains until tools receive
separate user acceptance.

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
for the host permission gate. Persistent permission grants are not implemented in step one; the adapter uses
the SDK's default `GrantWriter` behavior.

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

Keep the current action files and legacy augmentation registrations intact; suppress activation when tools take precedence. Tools must never call `TryHandleActionInference`,
`SpotifyActionHandler`, or construct synthetic `ServerActionMessage` objects. Actions must never dispatch through
the tool catalog. Both may call transport-neutral domain clients/services. If a useful operation currently lives
inside an action handler, extract a domain operation returning data/results, then give actions and tools their own
presentation adapters. Do not share action-specific note/reply plumbing with tools.

During integration, expose a separate opt-in tools augmentation (`<existing augmentation key>_tools`) and keep the
existing action augmentation key for compatibility. Implement the SDK tools-service marker on the service and plan
the tool instance independently. Selecting tools alone must not create an action instance or require ActionInference.
Users can select tools alone or legacy actions alone for comparison. Do not automatically select both, which
could cause duplicate device commands. Spotify tools default enabled following user runtime feedback; the
augmentation itself remains opt-in. Other migrations should choose defaults explicitly and version the change.

Use `Read` for queries and `Act` for changes to playback, libraries, or lights. Do not force `AlwaysAllowed`.
Mark results containing third-party text with the SDK's external-content metadata as appropriate. Describe results
as facts/data for the model, without instructions to trigger another reply. Tool execution must not call action
feedback helpers, `SendNoteAsync`, `SendNoteAttachmentAsync`, cancel a reply, or trigger another generation to report
its result. Return map images through `ChatToolCallResult.Images` instead.

## Migration coverage

Recommended order: OpenWeather, Hue, Spotify. Prefix the names below as described above; retain every legacy action.

| Module | Existing actions to cover | Migration-specific behavior |
| --- | --- | --- |
| OpenWeather (5) | `get_weather`, `get_weather_forecast`, `get_air_pollution`, `get_air_pollution_forecast`, `get_weather_map` | Reuse `IOpenWeatherClient` and summarizers. Preserve configured location fallback, units, selected details, and cache path. Separate data/formatting from note feedback. Return maps as images plus descriptive text. All five are queries. |
| Hue (8) | `turn_lights_on`, `turn_lights_off`, `hueBridge_connect`, `change_color`, `show_emotion`, `change_brightness`, `activate_scene`, `show_hue_inventory` | Reuse Hue manager/services. Validate brightness and target selection; expose useful disconnected/not-found errors. Inventory is Read; device/connection changes are Act. Keep emotion control gated by the configured character light and opt-in tool selection; agent-loop timing is distinct from the old after-assistant action timing. Preserve setup/link-button interaction separately from tool-result feedback. |
| Spotify (17) | `toggle_playback`, `spotify_connect`, `play_random_music`, `play_special_playlist`, `play_music`, `queue_track`, `volume`, `seek_playback`, `skip_next`, `skip_previous`, `repeat_mode`, `shuffle_mode`, `add_to_favorites`, `get_playlists`, `add_to_playlist`, `list_devices`, `transfer_to_device` | Reuse manager/search services. The connect tool diagnoses available players without activating one. Connect and playlist/device listing are Read; mutations are Act. Preserve configured playlists, playback state, last-command tracking, and speech ducking independently of the action handler. Keep OAuth setup and passive playback monitoring separate from tool execution. |

Spotify speech events use the neutral `SpotifyPlaybackSession` and `SpotifySpeechDucking`, independently of
`SpotifyActionHandler`. Preserve this boundary when extending the tools path.
Spotify playback tools use the active player shown in context, including an active paused player. When Spotify
reports no active player, the assistant asks the user to open Spotify on the intended device, select a track or
playlist and start playback manually, then waits for confirmation that playback has started. Discovery and transfer
serve explicit user requests to change destination; they are not missing-player recovery steps. Standard recovery
and transfer-name guidance is appended alongside saved description overrides. Explicit no-active-player errors
produce manual-start guidance; generic 404 or device errors are not assumed to mean there is no active player.
The tools session awaits a read-only player-state preflight before activation completes. The existing
`SpotifyPlaybackMonitor` owns stable connection, active-device, playback and track contexts, refreshed every second.
It does not discover devices at startup or in the background. No active player is distinct from a provider failure,
missing authorization, or an active paused player. Full context comparison detects device switches and track removal
without republishing unchanged ticks; context writes preserve the last command.
On a requested destination change, `spotify_list_devices` reports names, types, active status and control availability.
Transfer accepts device names and resolves current internal IDs privately. Neither discovery output nor playback
context exposes IDs, so approval arguments use names. Duplicate or ambiguous names require clarification or renaming
in Spotify. Restricted devices and devices without IDs are rejected. Transfer success means Spotify accepted the
transfer, not that playback is ready; it preserves the playing/paused state. No tool silently transfers or retries a
mutation. The legacy name-to-ID adapter remains separate, and the legacy connect tool remains available to disable.
`SpotifyManager.PlaySpecificUri` starts individual tracks through their album context with a track offset,
avoiding desktop clients that accept a bare `uris` request but clear the player. Album lookup must succeed
before changing playback; Spotify may continue with later album tracks after the selected song. After one
play command, up to five playback-state reads at 400 ms intervals confirm the requested track/episode or
context is playing. Acceptance without confirmation is a tool error, not success; no play command is retried.
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
catalog/handler against a fake manager: all 17 operations, mutation failures, input validation, missing state,
ambiguous devices, cancellation, metadata/settings defaults, ducking overlap and volume changes, session dispatch,
playback-only preflight, manual-start recovery guidance, paused/playing states, private name-to-ID resolution,
stable context identities, device switches, provider failures and recovery,
legacy suppression, disposal, tool-card settings, description overrides, defaults/migration, redirect validation, and
single-flow authentication settings. `SpotifyVoxtaOAuthChecks` covers automatic browser/desktop callbacks,
exact consent/exchange URI matching across transient provider instances, request-origin/path validation,
user/module isolation, expiry, changed credentials, overlapping-client guards, sign-out, cancellation, no retry,
encrypted storage, missing tokens, serialized refresh, and invalid grants. `SpotifyPlaybackChecks` uses the real SDK
with a fake HTTP transport to cover album-context requests, resolved track URIs, delayed confirmation, empty or
wrong playback, lookup failures, playlist contexts and cancellation without replaying commands. These checks make no Spotify network
calls and do not replace live browser/desktop acceptance testing. The user verified native desktop sign-in before
listener removal; automatic client switching still requires runtime acceptance after this change.

For each migration, add handler checks with fake domain clients for all operations, invalid inputs, provider failure,
and cancellation. Then test in Voxta with tools selected and legacy actions deselected: discovery/settings, host
permissions, successful and failed calls, multiple calls in one reply, cancellation, and results in the same agent
response. Verify no ActionInference requirement, duplicate notes, follow-up generation, or repeated side effects.
Repeat representative requests with only legacy actions selected. Test real maps, Hue devices and emotion control,
Spotify playback and ducking against their providers before declaring behavioral parity.

Each externally visible migration increments the affected module's manifest version (use a prerelease while testing),
then follows `AGENTS.md`'s locked build, commit, push, publish, workflow, and release-asset verification flow. Update
the SDK reference and lockfile with the manifest range only if a newer API is required. Delete legacy files only
after the tools have been verified and the user authorizes removal.
