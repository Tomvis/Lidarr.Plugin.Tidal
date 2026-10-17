using NUnit.Framework;
using NzbDrone.Core.Indexers.Tidal;

namespace Lidarr.Plugin.Tidal.Tests
{
    [TestFixture]
    public class TrackAlbumLookupsFixture
    {
        [Test]
        public void skips_albums_already_in_the_album_results()
        {
            var ids = TrackAlbumLookups.Select(new[] { "a1" }, new[] { "a1", "a2" }, 10);
            Assert.That(ids, Is.EqualTo(new[] { "a2" }));
        }

        [Test]
        public void looks_up_each_album_once_in_result_order()
        {
            var ids = TrackAlbumLookups.Select(new string[0], new[] { "a3", "a2", "a3", "a2", "a1" }, 10);
            Assert.That(ids, Is.EqualTo(new[] { "a3", "a2", "a1" }));
        }

        [Test]
        public void caps_the_number_of_lookups()
        {
            var ids = TrackAlbumLookups.Select(new string[0], new[] { "a1", "a2", "a3", "a4" }, 2);
            Assert.That(ids, Is.EqualTo(new[] { "a1", "a2" }));
        }
    }
}
