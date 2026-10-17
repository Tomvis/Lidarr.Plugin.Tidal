using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Indexers.Tidal
{
    // Tomvis fork (MUSIC-113): which track-result albums the parser fetches in full. Each one is a
    // serial API call inside a search, so look each album up once and cap the count (up to 300
    // track results per search used to mean up to 300 calls, repeats included).
    public static class TrackAlbumLookups
    {
        public static IReadOnlyList<string> Select(IEnumerable<string> albumResultIds, IEnumerable<string> trackAlbumIds, int max)
        {
            var known = new HashSet<string>(albumResultIds);
            return trackAlbumIds
                .Where(id => known.Add(id))
                .Take(max)
                .ToList();
        }
    }
}
