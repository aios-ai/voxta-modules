using HueApi.Models;
using Microsoft.Extensions.Logging;

namespace Voxta.Modules.Aios.PhilipsHue.Clients;

public class HueManager
{
    private readonly IHueBridgeConnectionService _bridgeConnectionService;
    private readonly IHueDataService _dataService;
    private readonly IHueCommandService _commandService;
    private readonly IColorConverterService _colorConverterService;
    private readonly ILogger<HueManager> _logger;

    private bool _initialized;
    public bool IsConnected => _initialized && _bridgeConnectionService.IsConnected && !_commandService.IsConnectionUnavailable;
    public bool IsAuthorizationRequired => _bridgeConnectionService.IsAuthorizationRequired;
    public HueBridgeState State { get; private set; } = HueBridgeState.Disconnected;
    public string? LastUserVisibleError { get; private set; }

    public HueManager(
        IHueBridgeConnectionService bridgeConnectionService,
        IHueDataService dataService,
        IHueCommandService commandService,
        IColorConverterService colorConverterService,
        ILogger<HueManager> logger)
    {
        _bridgeConnectionService = bridgeConnectionService;
        _dataService = dataService;
        _commandService = commandService;
        _colorConverterService = colorConverterService;
        _logger = logger;
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        _initialized = false;
        try
        {
            var connected = await _bridgeConnectionService.InitializeBridgeAsync(cancellationToken);
            State = _bridgeConnectionService.State;
            LastUserVisibleError = _bridgeConnectionService.LastUserVisibleError;

            if (!connected)
                return false;

            var dataLoaded = await _dataService.RetrieveBridgeDataAsync();
            cancellationToken.ThrowIfCancellationRequested();
            LastUserVisibleError = _dataService.LastUserVisibleError;
            if (!dataLoaded)
            {
                State = HueBridgeState.Unavailable;
                return false;
            }

            State = HueBridgeState.Connected;
            _initialized = true;
            LastUserVisibleError = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hue initialization failed unexpectedly.");
            State = HueBridgeState.Unavailable;
            LastUserVisibleError = "Hue could not initialize. Please check the bridge connection and try again.";
            return false;
        }
    }

    public string? TranslateColorNameToHex(string colorName)
    {
        return _colorConverterService.TranslateColorNameToHex(colorName);
    }

    public async Task<bool> SendHueCommandAsync(Guid targetId, string type, bool? state = null, string? color = null, double? brightness = null, string? scene = null, CancellationToken cancellationToken = default)
    {
        var succeeded = await _commandService.SendHueCommandAsync(targetId, type, state, color, brightness, scene, cancellationToken);
        LastUserVisibleError = _commandService.LastUserVisibleError ?? _bridgeConnectionService.LastUserVisibleError;
        State = succeeded ? HueBridgeState.Connected : _bridgeConnectionService.State;
        return succeeded;
    }

    public async Task<bool> ControlAllLightsAsync(bool turnOn, CancellationToken cancellationToken = default)
    {
        var succeeded = await _commandService.ControlAllLightsAsync(turnOn, cancellationToken);
        LastUserVisibleError = _commandService.LastUserVisibleError ?? _bridgeConnectionService.LastUserVisibleError;
        State = succeeded ? HueBridgeState.Connected : _bridgeConnectionService.State;
        return succeeded;
    }

    public IList<Light> GetLights() => _dataService.Lights;
    public IList<GroupedLight> GetGroups() => _dataService.Groups;
    public IList<Room> GetRooms() => _dataService.Rooms;
    public IList<Zone> GetZones() => _dataService.Zones;
    public IList<Scene> GetScenes() => _dataService.Scenes;
}
