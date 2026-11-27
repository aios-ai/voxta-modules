namespace Voxta.Modules.PhilipsHue.Clients;

public interface IColorConverterService
{
    string? TranslateColorNameToHex(string colorName);
}
