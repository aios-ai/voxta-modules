using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.OpenWeather.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Configuration;

public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload => [ApiKey.Name, "OpenWeatherToolCards", OpenWeatherToolsSettings.CardsFieldName, OpenWeatherToolsSettings.Styles.Name];
    private static readonly string DefaultTileCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voxta",
        "Aios.OpenWeather");

    public static readonly FormPasswordField ApiKey = new()
    {
        Name = "ApiKey",
        Label = "API Key",
        Required = true,
        //language=markdown
        Text = "Create an API key at [openweathermap.org](https://openweathermap.org/api).",
    };

    public static readonly FormTextField MyLocation = new()
    {
        Name = "MyLocation",
        Label = "My Location",
        DefaultValue = "",
        Text = "A default location if you want the AI to know where you are. E.g. 'New York, United States', 'zip:10115,DE', or '52.52,13.405' (latitude,longitude).",
    };

    public static readonly FormChoicesField Units = new()
    {
        Name = "Units",
        Label = "Units",
        Choices =
        [
            new FormChoice { Value = "metric", Label = "Metric (Celsius, m/s)"},
            new FormChoice { Value = "imperial", Label = "Imperial (Fahrenheit, miles/hour)"},
        ],
        DefaultValue = "metric",
    };

    public static readonly FormMultipleChoicesField WeatherDetails = new()
    {
        Name = "WeatherDetails",
        Label = "Default weather details",
        Text = "Used for general requests. Explicitly requested fields override these defaults, including unchecked fields.",
        Choices =
        [
            new() { Label = "Temp", Value = "Temp", Metadata = new FormChoiceMetadata { Subtitle = "Current temperature." } },
            new() { Label = "FeelsLike", Value = "FeelsLike", Metadata = new FormChoiceMetadata { Subtitle = "Feels-like temperature." } },
            new() { Label = "Precipitation", Value = "Precipitation", Metadata = new FormChoiceMetadata { Subtitle = "Rain and snow precipitation." } },
            new() { Label = "TempMinMax", Value = "TempMinMax", Metadata = new FormChoiceMetadata { Subtitle = "Minimum and maximum temperature." } },
            new() { Label = "Wind", Value = "Wind", Metadata = new FormChoiceMetadata { Subtitle = "Wind speed and direction." } },
            new() { Label = "CloudCover", Value = "CloudCover", Metadata = new FormChoiceMetadata { Subtitle = "Cloud coverage percentage." } },
            new() { Label = "Visibility", Value = "Visibility", Metadata = new FormChoiceMetadata { Subtitle = "Visibility in kilometers." } },
            new() { Label = "Humidity", Value = "Humidity", Metadata = new FormChoiceMetadata { Subtitle = "Relative humidity (%)." } },
            new() { Label = "Pressure", Value = "Pressure", Metadata = new FormChoiceMetadata { Subtitle = "Sea-level atmospheric pressure (hPa)." } },
            new() { Label = "SeaLevelPressure", Value = "SeaLevelPressure", Metadata = new FormChoiceMetadata { Subtitle = "Explicit sea-level pressure where supplied (hPa)." } },
            new() { Label = "GroundPressure", Value = "GroundPressure", Metadata = new FormChoiceMetadata { Subtitle = "Ground-level pressure (hPa)." } },
            new() { Label = "WindGust", Value = "WindGust", Metadata = new FormChoiceMetadata { Subtitle = "Wind gust speed." } },
            new() { Label = "Sunrise", Value = "Sunrise", Metadata = new FormChoiceMetadata { Subtitle = "Sunrise in the destination local time." } },
            new() { Label = "Sunset", Value = "Sunset", Metadata = new FormChoiceMetadata { Subtitle = "Sunset in the destination local time." } },
            new() { Label = "ObservationTime", Value = "ObservationTime", Metadata = new FormChoiceMetadata { Subtitle = "Time of the weather data." } },
            new() { Label = "PrecipitationProbability", Value = "PrecipitationProbability", Metadata = new FormChoiceMetadata { Subtitle = "Forecast precipitation probability (%); unavailable for current weather." } },
        ],
        StartValue = ["Temp", "TempMinMax", "Precipitation"],
    };

    public static readonly FormMultipleChoicesField PollutionDetails = new()
    {
        Name = "PollutionDetails",
        Label = "Default air pollution details",
        Text = "Used for general requests. Explicitly requested pollutants override these defaults, including unchecked fields.",
        Choices =
        [
            new() { Label = "AQI", Value = "AQI", Metadata = new FormChoiceMetadata { Subtitle = "Air Quality Index." } },
            new() { Label = "CO", Value = "CO", Metadata = new FormChoiceMetadata { Subtitle = "Carbon monoxide." } },
            new() { Label = "NO", Value = "NO", Metadata = new FormChoiceMetadata { Subtitle = "Nitric oxide." } },
            new() { Label = "NO2", Value = "NO2", Metadata = new FormChoiceMetadata { Subtitle = "Nitrogen dioxide." } },
            new() { Label = "O3", Value = "O3", Metadata = new FormChoiceMetadata { Subtitle = "Ozone." } },
            new() { Label = "SO2", Value = "SO2", Metadata = new FormChoiceMetadata { Subtitle = "Sulfur dioxide." } },
            new() { Label = "PM2.5", Value = "PM2.5", Metadata = new FormChoiceMetadata { Subtitle = "Particulate matter smaller than 2.5 micrometers." } },
            new() { Label = "PM10", Value = "PM10", Metadata = new FormChoiceMetadata { Subtitle = "Particulate matter smaller than 10 micrometers." } },
            new() { Label = "NH3", Value = "NH3", Metadata = new FormChoiceMetadata { Subtitle = "Ammonia." } },
        ],
        StartValue = ["AQI", "PM2.5", "PM10", "NO2", "O3"],
    };

    public static readonly FormTextField TileCachePath = new()
    {
        Name = "TileCachePath",
        Label = "Tile Cache Path",
        Required = true,
        Text = "The path to store the OpenStreetMap tile cache.",
        DefaultValue = DefaultTileCachePath,
        Advanced = true,
    };

    public Task<FormField[]> GetModuleConfigurationFieldsAsync(
         IAuthenticationContext auth,
         ISettingsSource settings,
         CancellationToken cancellationToken
     )
    {
        var fields = FormBuilder.Build(
            FormDocumentationField.Create(
                //language=markdown
                """
               For this provider to work we rely on a free external API service: [openweathermap.org](https://openweathermap.org/). They have a free plan which is rate limited to max 60 calls per minute.

               1. Open the URL and register
               2. Once you are registered and signed in, go to your profile and click on "My API keys"
               3. Give your key a custom name and hit Generate (It can take a while till the API key is activated, check your emails)
               """),
            ApiKey,
            MyLocation,
            Units,
            OpenWeatherToolsSettings.ConfigurationField(settings),
            OpenWeatherToolsSettings.Styles.AsField(),
            WeatherDetails,
            PollutionDetails,
            TileCachePath
        );
        return Task.FromResult(fields);
    }
}
