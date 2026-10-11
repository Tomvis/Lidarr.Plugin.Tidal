using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    // Tomvis fork (MUSIC-64): Lidarr wants only some tracks of an album, so download just those.
    public record WantedTrack(string Title, int Disc, int Number, int DurationMs);

    public record TidalAlbumTrack(string Id, string Title, int Disc, int Number, int DurationSec);

    public static class WantedTrackFilter
    {
        private const int DurationToleranceSec = 10;

        // Tidal track ids to download, or null for the whole album: nothing is unwanted, or a wanted
        // track has no match (a partial grab would leave it missing and the next search re-grab this album).
        public static List<string> Select(IReadOnlyList<TidalAlbumTrack> tidal, IReadOnlyList<WantedTrack> wanted, int releaseTrackCount)
        {
            if (wanted == null || wanted.Count == 0)
            {
                return null;
            }

            var picked = new List<string>();
            foreach (var want in wanted)
            {
                var match = ByTitle(tidal, want) ?? ByPosition(tidal, want, releaseTrackCount);
                if (match == null)
                {
                    return null;
                }

                if (!picked.Contains(match.Id))
                {
                    picked.Add(match.Id);
                }
            }

            return picked;
        }

        private static TidalAlbumTrack ByTitle(IReadOnlyList<TidalAlbumTrack> tidal, WantedTrack want)
        {
            var title = Normalize(want.Title);
            if (title.Length == 0)
            {
                return null;
            }

            var candidates = tidal.Where(t => Normalize(t.Title) == title && DurationFits(t, want)).ToList();

            return candidates.FirstOrDefault(t => t.Disc == want.Disc && t.Number == want.Number)
                ?? candidates.OrderBy(t => DurationGap(t, want)).FirstOrDefault();
        }

        // Same tracklist length: trust disc/number when the durations agree (titles differ by translation etc.)
        private static TidalAlbumTrack ByPosition(IReadOnlyList<TidalAlbumTrack> tidal, WantedTrack want, int releaseTrackCount)
        {
            if (tidal.Count != releaseTrackCount || want.DurationMs <= 0)
            {
                return null;
            }

            return tidal.FirstOrDefault(t => t.Disc == want.Disc && t.Number == want.Number && t.DurationSec > 0 && DurationFits(t, want));
        }

        private static bool DurationFits(TidalAlbumTrack track, WantedTrack want)
        {
            return want.DurationMs <= 0 || track.DurationSec <= 0 || DurationGap(track, want) <= DurationToleranceSec;
        }

        private static int DurationGap(TidalAlbumTrack track, WantedTrack want)
        {
            return Math.Abs(track.DurationSec - (int)Math.Round(want.DurationMs / 1000.0));
        }

        // "Song (feat. X) [2009 Remaster]" and "Song - 2009 Remaster" both become "song"
        public static string Normalize(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var t = Regex.Replace(title, @"\([^)]*\)|\[[^\]]*\]", " ");
            t = Regex.Replace(t, @"\s+-\s+[^-]*\b(remaster(ed)?|version|mix|edit|live|mono|stereo|demo|bonus)\b.*$", " ", RegexOptions.IgnoreCase);
            t = t.Normalize(NormalizationForm.FormD);

            var sb = new StringBuilder();
            foreach (var c in t)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }
    }
}
