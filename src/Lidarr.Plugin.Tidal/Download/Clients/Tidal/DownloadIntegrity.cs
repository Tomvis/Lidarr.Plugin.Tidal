using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    // Tomvis fork (MUSIC-149): a remuxed FLAC keeps the source's STREAMINFO, so a truncated track still
    // claims its full length and ffmpeg/Lidarr accept it. Only decoding shows how much audio is there.
    public static class DownloadIntegrity
    {
        private const double ToleranceSec = 2;

        private static readonly Regex OutTime = new(@"^out_time_us=(\d+)\s*$", RegexOptions.Multiline);

        // seconds decoded, from `ffmpeg -progress pipe:1` output; null when it reported none
        public static double? ParseDecodedSeconds(string progressOutput)
        {
            var matches = OutTime.Matches(progressOutput ?? string.Empty);
            if (matches.Count == 0)
            {
                return null;
            }

            return long.Parse(matches[matches.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture) / 1_000_000.0;
        }

        public static bool IsComplete(double decodedSec, int expectedSec)
        {
            return expectedSec <= 0 || decodedSec >= expectedSec - ToleranceSec;
        }
    }
}
