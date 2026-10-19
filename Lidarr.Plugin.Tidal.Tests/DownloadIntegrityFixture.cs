using NUnit.Framework;
using NzbDrone.Core.Download.Clients.Tidal;

namespace Lidarr.Plugin.Tidal.Tests
{
    [TestFixture]
    public class DownloadIntegrityFixture
    {
        [Test]
        public void reads_the_last_progress_time()
        {
            var output = "out_time_us=1000000\nprogress=continue\nout_time_us=105984000\nprogress=end\n";
            Assert.That(DownloadIntegrity.ParseDecodedSeconds(output), Is.EqualTo(105.984).Within(0.0001));
        }

        [Test]
        public void no_progress_line_means_nothing_decoded()
        {
            Assert.That(DownloadIntegrity.ParseDecodedSeconds(""), Is.Null);
            Assert.That(DownloadIntegrity.ParseDecodedSeconds("out_time_us=N/A\nprogress=end\n"), Is.Null);
        }

        [Test]
        public void full_length_track_is_complete()
        {
            Assert.That(DownloadIntegrity.IsComplete(265.56, 266), Is.True);
            Assert.That(DownloadIntegrity.IsComplete(265.56, 265), Is.True);
        }

        [Test]
        public void truncated_track_is_not_complete()
        {
            // MUSIC-149: At the Gates "Garden of Cyrus" decoded 106 s of 266 s
            Assert.That(DownloadIntegrity.IsComplete(105.984, 266), Is.False);
            // In Vain "En forgangen tid" decoded 96%
            Assert.That(DownloadIntegrity.IsComplete(371.3, 387), Is.False);
        }

        [Test]
        public void unknown_duration_cannot_be_checked()
        {
            Assert.That(DownloadIntegrity.IsComplete(10, 0), Is.True);
        }
    }
}
