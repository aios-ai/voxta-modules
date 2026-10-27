namespace Voxta.Modules.Aios.PhilipsHue.Clients;

internal static class HuePairingRequest
{
    internal const string Message = "Please press the link button on your Hue bridge to authorize the connection.";
    private static readonly Lazy<string> Logo = new(() =>
    {
        using var stream = typeof(VoxtaModule).Assembly.GetManifestResourceStream(
            "Voxta.Modules.Aios.PhilipsHue.Assets.PhilipsHue.png")
            ?? throw new InvalidOperationException("The embedded Hue logo is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
    });
    internal static string ImageUrl => Logo.Value;
}
