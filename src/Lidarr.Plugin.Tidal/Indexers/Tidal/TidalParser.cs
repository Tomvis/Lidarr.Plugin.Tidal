using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Tidal;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalParser : IParseIndexerResponse
    {
        public TidalIndexerSettings Settings { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();
            var content = new HttpResponse<TidalSearchResponse>(response.HttpResponse).Content;

            var jsonResponse = JObject.Parse(content).ToObject<TidalSearchResponse>();
            var releases = jsonResponse.AlbumResults.Items.Select(result => ProcessAlbumResult(result)).ToArray();

            foreach (var task in releases)
            {
                torrentInfos.AddRange(task);
            }

            foreach (var track in jsonResponse.TrackResults.Items)
            {
                // make sure the album hasn't already been processed before doing this
                if (!jsonResponse.AlbumResults.Items.Any(a => a.Id == track.Album.Id))
                {
                    var processTrackTask = ProcessTrackAlbumResultAsync(track);
                    processTrackTask.Wait();
                    if (processTrackTask.Result != null)
                        torrentInfos.AddRange(processTrackTask.Result);
                }
            }

            return torrentInfos
                .OrderByDescending(o => o.Size)
                .ToArray();
        }

        private IEnumerable<ReleaseInfo> ProcessAlbumResult(TidalSearchResponse.Album result)
        {
            // determine available audio qualities
            List<AudioQuality> qualityList = new() { AudioQuality.LOW, AudioQuality.HIGH };

            if (result.MediaMetadata.Tags.Contains("HIRES_LOSSLESS"))
            {
                qualityList.Add(AudioQuality.LOSSLESS);
                qualityList.Add(AudioQuality.HI_RES_LOSSLESS);
            }
            else if (result.MediaMetadata.Tags.Contains("LOSSLESS"))
                qualityList.Add(AudioQuality.LOSSLESS);

            var quality = Enum.Parse<AudioQuality>(result.AudioQuality);
            var edition = GetAlbumEdition(result);
            return qualityList.Select(q => ToReleaseInfo(result, q, edition));
        }

        private async Task<IEnumerable<ReleaseInfo>> ProcessTrackAlbumResultAsync(TidalSearchResponse.Track result)
        {
            try
            {
                var album = (await TidalAPI.Instance.Client.API.GetAlbum(result.Album.Id)).ToObject<TidalSearchResponse.Album>(); // track albums hold much less data so we get the full one
                return ProcessAlbumResult(album);
            }
            catch (ResourceNotFoundException) // seems to occur in some cases, not sure why. i blame tidal
            {
                return null;
            }
        }

        private static ReleaseInfo ToReleaseInfo(TidalSearchResponse.Album x, AudioQuality bitrate, string edition)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (DateTime.TryParse(x.ReleaseDate, out var digitalReleaseDate))
            {
                publishDate = digitalReleaseDate;
                year = publishDate.Year;
            }
            else if (DateTime.TryParse(x.StreamStartDate, out var startStreamDate))
            {
                publishDate = startStreamDate;
                year = startStreamDate.Year;
            }

            var url = x.Url;

            var result = new ReleaseInfo
            {
                Guid = $"Tidal-{x.Id}-{bitrate}",
                Artist = x.Artists.First().Name,
                Album = x.Title,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(TidalDownloadProtocol)
            };

            string format;
            switch (bitrate)
            {
                case AudioQuality.LOW:
                    result.Codec = "AAC";
                    result.Container = "96";
                    format = "AAC (M4A) 96kbps";
                    break;
                case AudioQuality.HIGH:
                    result.Codec = "AAC";
                    result.Container = "320";
                    format = "AAC (M4A) 320kbps";
                    break;
                case AudioQuality.LOSSLESS:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC (M4A) Lossless";
                    break;
                case AudioQuality.HI_RES_LOSSLESS:
                    result.Codec = "FLAC";
                    result.Container = "24bit Lossless";
                    format = "FLAC (M4A) 24bit Lossless";
                    break;
                default:
                    throw new NotImplementedException();
            }

            // estimated sizing as tidal doesn't provide exact sizes in its api
            var bps = bitrate switch
            {
                AudioQuality.HI_RES_LOSSLESS => 1152000,
                AudioQuality.LOSSLESS => 176400,
                AudioQuality.HIGH => 40000,
                AudioQuality.LOW => 12000,
                _ => 40000
            };
            var size = x.Duration * bps;

            result.Size = size;
            result.Title = $"{x.Artists.First().Name} - {x.Title}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            // Edition/remaster tag. Tidal sometimes labels editions at the album level
            // ("version"), but it mirrors that into the album title, so tagging it again
            // is redundant. The genuinely-hidden case is a remaster labelled only on the
            // track titles (e.g. "Song (2009 Remaster)") while the album stays clean.
            // GetAlbumEdition() surfaces that, de-duped against the album title.
            if (!string.IsNullOrWhiteSpace(edition))
            {
                result.Title += $" [{edition}]";
            }

            // Immersive-audio editions (Dolby Atmos / 360 Reality Audio) are separate
            // Tidal albums that often share the same title; tag them too. STEREO is the
            // default and is never tagged.
            foreach (var mode in GetAudioModeTags(x.AudioModes))
            {
                result.Title += $" [{mode}]";
            }

            if (x.Explicit)
            {
                result.Title += " [Explicit]";
            }

            result.Title += $" [{format}] [WEB]";

            return result;
        }

        private static IEnumerable<string> GetAudioModeTags(string[] audioModes)
        {
            if (audioModes == null)
            {
                yield break;
            }

            foreach (var mode in audioModes)
            {
                switch (mode)
                {
                    case "DOLBY_ATMOS":
                        yield return "Dolby Atmos";
                        break;
                    case "SONY_360RA":
                        yield return "360 Reality Audio";
                        break;
                }
            }
        }

        // Cache of albumId -> derived edition ("" = checked, none found) so repeated
        // searches (RSS/wanted) don't re-fetch tracks for the same album every time.
        private static readonly ConcurrentDictionary<string, string> _editionCache = new();

        // A trailing parenthetical that names an album-wide edition, e.g. "(2009 Remaster)".
        // Restricted to edition-ish keywords so per-track variants like "(feat. X)" or
        // "(Live)" don't get mistaken for an album edition.
        private static readonly Regex EditionSuffixRegex = new(
            @"\(([^()]*\b(?:remaster(?:ed)?|re-?master|mono|stereo|deluxe|anniversary|expanded|reissue)\b[^()]*)\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Returns an edition string to tag the album with, or null. When Tidal already
        // labels the album ("version") it mirrors it into the title, so we don't tag it
        // again; otherwise we look for an edition labelled only on the track titles.
        private static string GetAlbumEdition(TidalSearchResponse.Album album)
        {
            var candidate = !string.IsNullOrWhiteSpace(album.Version)
                ? album.Version.Trim()
                : DeriveEditionFromTracks(album);

            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            // don't duplicate what's already in the album title
            if (!string.IsNullOrEmpty(album.Title) &&
                album.Title.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                return null;

            return candidate;
        }

        // Fetches the album's tracks and lifts a shared trailing edition suffix
        // (e.g. "2009 Remaster") when most tracks carry it. Costs one extra API call per
        // unlabelled album; the result is cached per album id for the process lifetime.
        private static string DeriveEditionFromTracks(TidalSearchResponse.Album album)
        {
            if (_editionCache.TryGetValue(album.Id, out var cached))
                return string.IsNullOrEmpty(cached) ? null : cached;

            JArray items;
            try
            {
                var response = TidalAPI.Instance.Client.API.GetAlbumTracks(album.Id).GetAwaiter().GetResult();
                items = response?["items"] as JArray;
            }
            catch
            {
                // transient network/parse issue - skip the tag, don't cache so we retry later
                return null;
            }

            string edition = null;
            if (items != null && items.Count > 0)
            {
                var matches = items
                    .Select(t => (string)t["title"])
                    .Where(title => !string.IsNullOrEmpty(title))
                    .Select(title => EditionSuffixRegex.Match(title))
                    .Where(m => m.Success)
                    .Select(m => m.Groups[1].Value.Trim())
                    .ToList();

                if (matches.Count > 0)
                {
                    var top = matches
                        .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count())
                        .First();

                    // require the edition to be shared by most tracks, so a single
                    // oddly-named bonus track can't mislabel the whole album
                    if (top.Count() >= items.Count * 0.6)
                        edition = top.First();
                }
            }

            _editionCache[album.Id] = edition ?? "";
            return edition;
        }
    }
}
