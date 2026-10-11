using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    // Tomvis fork (MUSIC-64): Track.Monitored exists only in Tomvis/Lidarr, so read it by reflection and
    // keep building against upstream Lidarr. Upstream (no property) = every track wanted.
    public static class LidarrWantedTracks
    {
        private static readonly PropertyInfo Monitored = typeof(Track).GetProperty("Monitored");

        public static (List<WantedTrack> Wanted, int TrackCount) For(RemoteAlbum remoteAlbum, ITrackService trackService)
        {
            if (Monitored == null || remoteAlbum?.Albums == null || remoteAlbum.Albums.Count != 1)
            {
                return (null, 0);
            }

            var tracks = trackService.GetTracksByAlbum(remoteAlbum.Albums[0].Id);
            if (tracks.All(IsWanted))
            {
                return (null, tracks.Count);
            }

            var wanted = tracks.Where(IsWanted)
                .Select(t => new WantedTrack(t.Title, t.MediumNumber, t.AbsoluteTrackNumber, t.Duration))
                .ToList();

            return (wanted, tracks.Count);
        }

        private static bool IsWanted(Track track)
        {
            return (bool)Monitored.GetValue(track);
        }
    }
}
