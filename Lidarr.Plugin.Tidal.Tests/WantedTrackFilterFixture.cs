using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Download.Clients.Tidal;

namespace Lidarr.Plugin.Tidal.Tests
{
    [TestFixture]
    public class WantedTrackFilterFixture
    {
        private static readonly List<TidalAlbumTrack> Album = new()
        {
            new("t1", "Intro", 1, 1, 60),
            new("t2", "Song (2009 Remaster)", 1, 2, 240),
            new("t3", "Другая песня", 1, 3, 300),
            new("t4", "Song (Live)", 2, 1, 260),
        };

        [Test]
        public void no_wanted_list_downloads_everything()
        {
            Assert.That(WantedTrackFilter.Select(Album, null, 4), Is.Null);
        }

        [Test]
        public void matches_title_ignoring_edition_suffix()
        {
            var ids = WantedTrackFilter.Select(Album, new List<WantedTrack> { new("Song", 1, 2, 241000) }, 4);
            Assert.That(ids, Is.EqualTo(new[] { "t2" }));
        }

        [Test]
        public void same_title_prefers_same_position()
        {
            var ids = WantedTrackFilter.Select(Album, new List<WantedTrack> { new("Song (Live)", 2, 1, 258000) }, 4);
            Assert.That(ids, Is.EqualTo(new[] { "t4" }));
        }

        [Test]
        public void falls_back_to_position_when_title_differs_and_tracklist_matches()
        {
            var ids = WantedTrackFilter.Select(Album, new List<WantedTrack> { new("Another Song", 1, 3, 299000) }, 4);
            Assert.That(ids, Is.EqualTo(new[] { "t3" }));
        }

        [Test]
        public void no_position_fallback_when_tracklists_differ()
        {
            Assert.That(WantedTrackFilter.Select(Album, new List<WantedTrack> { new("Another Song", 1, 3, 299000) }, 5), Is.Null);
        }

        [Test]
        public void duration_mismatch_is_not_a_match()
        {
            Assert.That(WantedTrackFilter.Select(Album, new List<WantedTrack> { new("Song", 1, 2, 400000) }, 5), Is.Null);
        }

        [Test]
        public void one_unmatched_wanted_track_downloads_everything()
        {
            var wanted = new List<WantedTrack> { new("Intro", 1, 1, 60000), new("Missing", 1, 9, 100000) };
            Assert.That(WantedTrackFilter.Select(Album, wanted, 4), Is.Null);
        }

        [Test]
        public void several_wanted_tracks()
        {
            var wanted = new List<WantedTrack> { new("Intro", 1, 1, 0), new("song", 1, 2, 240000) };
            Assert.That(WantedTrackFilter.Select(Album, wanted, 4), Is.EqualTo(new[] { "t1", "t2" }));
        }

        [TestCase("Song - 2009 Remaster", "song")]
        [TestCase("Café (feat. X) [Live]", "cafe")]
        [TestCase("Stop! - Single Version", "stop")]
        public void normalize(string title, string expected)
        {
            Assert.That(WantedTrackFilter.Normalize(title), Is.EqualTo(expected));
        }
    }
}
