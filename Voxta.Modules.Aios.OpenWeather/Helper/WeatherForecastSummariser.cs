using System.Globalization;
using System.Text;
using Voxta.Modules.Aios.OpenWeather.Helper;
using Voxta.Modules.Aios.OpenWeather.Clients;

public static class WeatherForecastSummariser
{
    public static string Summarise(List<ForecastItem> forecastItems, CultureInfo culture, HashSet<string> weatherDetails, int days = 5, string unitSuffix = "", string windUnit = "m/s", int utcOffset = 0)
    {
        if (forecastItems == null || forecastItems.Count == 0)
            return "No forecast data available.";

        // dt_txt is UTC; shift by the destination offset before day/night grouping.
        DateTime Local(ForecastItem x) => DateTimeOffset.FromUnixTimeSeconds(x.Dt != 0 ? x.Dt :
            new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(x.DtTxt!, CultureInfo.InvariantCulture), DateTimeKind.Utc)).ToUnixTimeSeconds())
            .UtcDateTime.AddSeconds(utcOffset);
        var sb = new StringBuilder();
        
        var groupedByDate = forecastItems
            .GroupBy(f => Local(f).Date)
            .OrderBy(g => g.Key)
            .Take(days);

        foreach (var dayGroup in groupedByDate)
        {
            var date = dayGroup.Key;

            var dayBlock = dayGroup
                .Where(x =>
                {
                    var hour = Local(x).Hour;
                    return hour >= 8 && hour < 20;
                })
                .ToList();

            var nightBlock = dayGroup
                .Where(x =>
                {
                    var hour = Local(x).Hour;
                    return hour >= 20 || hour < 8;
                })
                .ToList();

            if (dayBlock.Any())
                sb.AppendLine(BuildBlockSummary(date, "Day", dayBlock, unitSuffix, culture, weatherDetails, windUnit));

            if (nightBlock.Any())
                sb.AppendLine(BuildBlockSummary(date, "Night", nightBlock, unitSuffix, culture, weatherDetails, windUnit));
        }

        return sb.ToString().Trim();
    }

    private static string BuildBlockSummary(
        DateTime date,
        string label,
        List<ForecastItem> block,
        string unitSuffix,
        CultureInfo culture,
        HashSet<string> weatherDetails, string windUnit)
    {
        var minTemp = block.Min(x => x.Main.TempMin);
        var maxTemp = block.Max(x => x.Main.TempMax);

        var commonCondition = block
            .GroupBy(x => x.Weather[0].Description)
            .OrderByDescending(g => g.Count())
            .First().Key;

        var totalRain = block.Sum(x => x.Rain?.ThreeHours ?? 0);
        var totalSnow = block.Sum(x => x.Snow?.ThreeHours ?? 0);

        var text = $"{date.ToString("yyyy-MM-dd dddd", culture)} {label}: ";
        if (weatherDetails.Contains("Condition")) text += culture.TextInfo.ToTitleCase(commonCondition) + ". ";
        if (weatherDetails.Contains("TempMinMax")) text += $"{FormatWholeNumber(minTemp)}-{FormatWholeNumber(maxTemp)}{unitSuffix}. ";
        else if (weatherDetails.Contains("Temp")) text += $"Temperature: {FormatWholeNumber(block.Average(x => x.Main.Temp))}{unitSuffix}. ";
        if (weatherDetails.Contains("FeelsLike")) text += $"Feels like: {FormatWholeNumber(block.Average(x => x.Main.FeelsLike))}{unitSuffix}. ";
        if (weatherDetails.Contains("Precipitation")) text += $"Rain: {totalRain:0.#} mm; snow: {totalSnow:0.#} mm. ";
        var extras = new StringBuilder();
        double? Average(Func<ForecastItem, double?> get) => block.Any(x => get(x).HasValue) ? block.Average(get) : null;
        WeatherReportFormatter.AppendWeatherExtras(extras, new MainInfo
        {
            Temp = 0, FeelsLike = 0, TempMin = 0, TempMax = 0,
            Humidity = Average(x => x.Main.Humidity), Pressure = Average(x => x.Main.Pressure),
            SeaLevelPressure = Average(x => x.Main.SeaLevelPressure), GroundPressure = Average(x => x.Main.GroundPressure),
        }, weatherDetails);
        text += extras;
        if (weatherDetails.Contains("WindGust")) text += $"Peak wind gust: {(block.Max(x => x.Wind.Gust) is { } gust ? gust.ToString("0.#", CultureInfo.InvariantCulture) + " " + windUnit : "unavailable")}. ";
        if (weatherDetails.Contains("PrecipitationProbability")) text += $"Highest three-hour precipitation probability: {(block.Max(x => x.Pop) is { } pop ? (pop * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%" : "unavailable")}. ";
        if (weatherDetails.Contains("ObservationTime")) text += "Forecast valid times are summarized by destination day/night; these are not observations. ";

        if (weatherDetails.Contains("Wind"))
        {
            var avgWindSpeed = block.Average(x => x.Wind.Speed);
            var avgWindDir = block.Average(x => x.Wind.Deg);
            text += $"Wind: {avgWindSpeed:0.#} {windUnit} at {FormatWholeNumber(avgWindDir)}°. ";
        }

        if (weatherDetails.Contains("CloudCover"))
        {
            var avgClouds = block.Average(x => x.Clouds.All);
            text += $", Cloud cover: {FormatWholeNumber(avgClouds)}%";
        }

        if (weatherDetails.Contains("Visibility"))
        {
            var visibilityValues = block
                .Where(x => x.Visibility.HasValue)
                .Select(x => x.Visibility!.Value)
                .ToList();
            if (visibilityValues.Count > 0)
            {
                var avgVisibility = visibilityValues.Average() / 1000.0;
                text += $", Visibility: {FormatWholeNumber(avgVisibility)} km";
            }
        }

        return text.TrimEnd(' ', '.') + ".";
    }

    private static string FormatWholeNumber(double value)
    {
        return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
    }
}
