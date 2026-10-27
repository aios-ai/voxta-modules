using Voxta.Modules.Aios.Spotify.Clients.Services;

internal static class SpotifyDuckingChecks
{
    public static async Task RunAsync()
    {
        foreach (var (mode, amount, start, expected) in new[]
        {
            (SpotifyDuckingMode.Fixed, 60, 30, 30),
            (SpotifyDuckingMode.Fixed, 60, 60, 60),
            (SpotifyDuckingMode.Fixed, 45, 60, 45),
            (SpotifyDuckingMode.Fixed, 100, 60, 60),
            (SpotifyDuckingMode.Relative, 25, 60, 45),
            (SpotifyDuckingMode.Relative, 25, 30, 23),
            (SpotifyDuckingMode.Relative, 0, 60, 60),
            (SpotifyDuckingMode.Relative, 100, 60, 0),
        })
        {
            var f = new Fixture();
            f.Backend.State!.Device.VolumePercent = start;
            var ducking = new SpotifySpeechDucking(f.Manager, () => f.Backend.State, amount, mode);
            await ducking.LowerVolumeAsync("speech", default);
            var calls = f.Backend.Calls.Where(c => c.Name == "ChangeVolume").ToArray();
            Check(expected == start ? calls.Length == 0 : calls.Length == 1 && (int)calls[0].Args[0]! == expected,
                "Incorrect ducking target: " + (mode, amount, start));
            await ducking.RestoreVolumeAsync("speech", default);
            if (expected != start)
                Check((int)f.Backend.Calls.Last().Args[0]! == start, "Original volume not restored");
        }

        {
            var f = new Fixture();
            f.Backend.State!.Device.VolumePercent = 60;
            var ducking = new SpotifySpeechDucking(f.Manager, () => f.Backend.State, 25, SpotifyDuckingMode.Relative);
            await ducking.LowerVolumeAsync("speech", default);
            // The monitor now sees the lowered value. Overlapping events must not compound the reduction.
            f.Backend.State.Device.VolumePercent = 45;
            await ducking.LowerVolumeAsync("transcription", default);
            await ducking.LowerVolumeAsync("speech", default);
            await ducking.RestoreVolumeAsync("speech", default);
            Check(f.Backend.Calls.Count(c => c.Name == "ChangeVolume") == 1, "Overlap compounded or restored early");
            await ducking.ChangeVolumeAsync("increase", 20, default);
            Check((int)f.Backend.Calls.Last().Args[0]! == 60, "Relative user change did not use unducked volume");
            await ducking.RestoreVolumeAsync("transcription", default);
            Check((int)f.Backend.Calls.Last().Args[0]! == 80, "User's desired volume not restored");
        }
        Console.WriteLine("PASS fixed and relative ducking, no volume increase, disabled modes, overlap and user volume changes");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
