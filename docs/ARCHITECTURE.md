# Tools foundation and migration contract

## Current boundary

The three installable modules use `IActionInferenceAugmentation`. OpenWeather and Hue define and execute
actions in their chat augmentation instances. Spotify defines actions in its augmentation instance and
executes them through `SpotifyActionHandler`; this handler also implements speech ducking. The existing
action configuration, registration, execution, and files are preserved.

Step one supplies a common tools foundation compiled into all three modules. It registers no tools and
changes no configuration or externally visible behavior. Module versions and compatibility ranges remain
unchanged; there is no new module release for this internal foundation.

## SDK contract

The owning API is the installed `Voxta.Sdk.Modules` / `Voxta.Abstractions` **1.13.0-alpha.10** package,
which is already the manifest compatibility floor. The foundation compiles against that version:

- `IToolsChatAugmentation` provides `GetToolsAsync` and `CallToolAsync` without requiring action inference.
- `IToolsChatAugmentationsService` is the SDK's tools-capable service marker, to apply when integrating tools.
- `ChatTool` owns name, description, JSON input schema, source, augmentation key, enabled state, chat styles,
  permission class, display mode, speech synchronization, and external-content metadata.
- `ChatToolSchema.FromArguments` builds schemas from SDK `FunctionArgumentDefinition` objects. Tool argument
  definitions must live in tool files; do not obtain them by calling the old action builders.
- `ChatToolSettingsContributions` supplies tool selection and chat-style settings. Use these instead of
  reproducing the SDK's settings UI. Tool settings must not read legacy `Actions` settings.
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

Keep the current action files and their registrations intact. Tools must never call `TryHandleActionInference`,
`SpotifyActionHandler`, or construct synthetic `ServerActionMessage` objects. Actions must never dispatch through
the tool catalog. Both may call transport-neutral domain clients/services. If a useful operation currently lives
inside an action handler, extract a domain operation returning data/results, then give actions and tools their own
presentation adapters. Do not share action-specific note/reply plumbing with tools.

During integration, expose a separate opt-in tools augmentation (`<existing augmentation key>_tools`) and keep the
existing action augmentation key for compatibility. Implement the SDK tools-service marker on the service and plan
the tool instance independently. Selecting tools alone must not create an action instance or require ActionInference.
Keep tools disabled by default during initial testing; users can select tools alone or legacy actions alone for
comparison. Do not automatically select both, which could cause duplicate device commands. There is no new selector
in step one. A later default switch must be an explicit externally visible versioned change.

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
| Spotify (17) | `toggle_playback`, `spotify_connect`, `play_random_music`, `play_special_playlist`, `play_music`, `queue_track`, `volume`, `seek_playback`, `skip_next`, `skip_previous`, `repeat_mode`, `shuffle_mode`, `add_to_favorites`, `get_playlists`, `add_to_playlist`, `list_devices`, `transfer_to_device` | Reuse manager/search services. `spotify_connect` currently reports that no client is active; do not invent a connection operation. Playlist/device listing are Read; mutations are Act. Preserve configured playlists, playback state, last-command tracking, and speech ducking independently of the action handler. Keep OAuth setup and passive playback monitoring separate from tool execution. |

Spotify's speech events currently call `SpotifyActionHandler` even outside action dispatch. Extract speech ducking
to a neutral playback component before wiring the tools path, so tools do not acquire an action-handler dependency.
Playback monitors and initial Hue inventory notes are session features; choose their owner explicitly when both
augmentations are selected to avoid duplicate polling/notes and double disposal.

## Validation and completion

Build all modules with locked packages:

```powershell
dotnet build Voxta.Modules.slnx -c Release -p:RestoreLockedMode=true -m:1
dotnet run --project tests/Tools.Tests -c Release -p:RestoreLockedMode=true
```

`Tools.Tests` is a standalone executable: assertions fail the process and successful checks print their names.
It covers SDK dispatch, metadata ownership, disabled/unknown tools, invalid argument envelopes, safe failures,
cancellation, and no automatic retry. It does not test live providers or host registration.

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
