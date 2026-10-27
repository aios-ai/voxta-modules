namespace Voxta.Modules.Aios.PhilipsHue.Clients;

public interface IHueCommandService
{
    string? LastUserVisibleError { get; }
    Task<bool> SendHueCommandAsync(Guid targetId, string type, bool? state = null, string? color = null, double? brightness = null, string? scene = null, CancellationToken cancellationToken = default);
    Task<bool> ControlAllLightsAsync(bool turnOn, CancellationToken cancellationToken = default);
}
