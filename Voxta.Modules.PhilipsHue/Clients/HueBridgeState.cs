namespace Voxta.Modules.PhilipsHue.Clients;

public enum HueBridgeState
{
    Disconnected,
    Connected,
    AuthRequired,
    Unavailable,
    MissingConfiguration
}
