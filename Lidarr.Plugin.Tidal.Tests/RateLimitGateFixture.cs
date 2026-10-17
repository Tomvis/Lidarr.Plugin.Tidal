using System;
using NUnit.Framework;
using TidalSharp;

namespace Lidarr.Plugin.Tidal.Tests
{
    [TestFixture]
    public class RateLimitGateFixture
    {
        private DateTime _now;
        private RateLimitGate _gate;

        [SetUp]
        public void SetUp()
        {
            _now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            _gate = new RateLimitGate(() => _now);
        }

        [Test]
        public void open_until_tripped()
        {
            Assert.That(_gate.IsCoolingDown, Is.False);
            Assert.That(_gate.Remaining, Is.EqualTo(TimeSpan.Zero));
        }

        [Test]
        public void backs_off_exponentially_without_retry_after()
        {
            Assert.That(_gate.Trip(1, null), Is.EqualTo(TimeSpan.FromSeconds(1)));
            _now += TimeSpan.FromSeconds(1);
            Assert.That(_gate.Trip(2, null), Is.EqualTo(TimeSpan.FromSeconds(2)));
            _now += TimeSpan.FromSeconds(2);
            Assert.That(_gate.Trip(3, null), Is.EqualTo(TimeSpan.FromSeconds(4)));
        }

        [Test]
        public void honors_retry_after_seconds()
        {
            Assert.That(_gate.Trip(1, "7"), Is.EqualTo(TimeSpan.FromSeconds(7)));
        }

        [Test]
        public void honors_retry_after_http_date()
        {
            Assert.That(_gate.Trip(1, _now.AddSeconds(12).ToString("R")), Is.EqualTo(TimeSpan.FromSeconds(12)));
        }

        [Test]
        public void caps_the_delay()
        {
            Assert.That(_gate.Trip(1, "3600"), Is.EqualTo(RateLimitGate.MaxDelay));
            _now += RateLimitGate.MaxDelay;
            Assert.That(_gate.Trip(20, null), Is.EqualTo(RateLimitGate.MaxDelay));
        }

        [Test]
        public void cools_down_for_every_caller_until_it_expires()
        {
            _gate.Trip(1, "5");
            _now += TimeSpan.FromSeconds(2);
            Assert.That(_gate.IsCoolingDown, Is.True);
            Assert.That(_gate.Remaining, Is.EqualTo(TimeSpan.FromSeconds(3)));
            _now += TimeSpan.FromSeconds(3);
            Assert.That(_gate.IsCoolingDown, Is.False);
        }

        [Test]
        public void a_shorter_trip_never_shortens_a_running_cooldown()
        {
            _gate.Trip(1, "10");
            Assert.That(_gate.Trip(1, null), Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void garbage_retry_after_falls_back_to_backoff()
        {
            Assert.That(_gate.Trip(2, "soon"), Is.EqualTo(TimeSpan.FromSeconds(2)));
        }
    }
}
