using NUnit.Framework;
using Unity.Mathematics;
using Unseen.Combat;
using Unseen.Core;

namespace Unseen.Tests
{
    /// <summary>
    /// How near a thrown blade has to pass to count.
    ///
    /// The one number that decides whether the shuriken feels like a weapon or like a puzzle. It
    /// was 0.42 m and throws that visibly connected were being called misses - which is the worst
    /// kind of miss, because the player has no way to tell it from a hit they did not deserve.
    ///
    /// Tested as arithmetic rather than through a match, because that is what it is. The body is a
    /// vertical segment from ankles to head and the blade is a segment through the air; whether
    /// they are within a radius of each other has nothing to do with a scene.
    /// </summary>
    public sealed class ShurikenHitWindowTests
    {
        /// <summary>A body standing at the origin: a segment from ankle height to head height.</summary>
        private static readonly float3 Low = new float3(0f, 0.25f, 0f);
        private static readonly float3 High = new float3(0f, 1.65f, 0f);

        private static bool Passes(float sideways, float radius)
        {
            // Thrown horizontally past the body at chest height, offset sideways by the given
            // distance. Ten metres of travel, so the blade is well past before the test ends.
            var from = new float3(sideways, 1.2f, -5f);

            return ShurikenSystem.SegmentsClose(from, new float3(0f, 0f, 1f), 10f,
                Low, High, radius, out float _);
        }

        [Test]
        public void ABladeThroughTheBodyHits()
        {
            Assert.IsTrue(Passes(0f, UnseenConfig.Default.Shuriken.HitRadiusMetres),
                "dead centre, which had better count");
        }

        [Test]
        public void AThrowThatBrushesThePlayerNowCounts()
        {
            float radius = UnseenConfig.Default.Shuriken.HitRadiusMetres;

            // Sixty centimetres off the centre line is inside the old 0.42 m window's failure range
            // and outside a body's own radius - the shot that looked like it grazed an arm and was
            // silently scored a miss. This is the case the change is for.
            Assert.IsTrue(Passes(0.6f, radius),
                "a blade passing close enough to draw blood should draw blood");
        }

        [Test]
        public void AThrowThatPlainlyMissesStillMisses()
        {
            float radius = UnseenConfig.Default.Shuriken.HitRadiusMetres;

            // The other side of the bargain. A forgiving window is not an excuse for a blade that
            // sails a metre and a half wide counting as a kill: that is the point where the weapon
            // stops being aimed and starts being pointed, and a player on the receiving end can
            // tell.
            Assert.IsFalse(Passes(1.5f, radius),
                "a metre and a half wide is a miss in any honest accounting");
        }

        [Test]
        public void TheWindowIsGenerousButNotAbsurd()
        {
            float radius = UnseenConfig.Default.Shuriken.HitRadiusMetres;

            // A body is roughly a third of a metre through the shoulders. Pinning the window inside
            // a range rather than to an exact number: the value is meant to be tuned by feel, and a
            // test that broke on every tweak would be deleted rather than consulted. What must not
            // happen is somebody quietly taking it back to something unhittable, or out to where it
            // is hitting people nobody aimed at.
            Assert.GreaterOrEqual(radius, 0.6f, "tighter than this was the complaint");
            Assert.LessOrEqual(radius, 1.0f, "wider than this is not aiming any more");
        }

        [Test]
        public void ABladeThatFallsShortDoesNotReachTheBody()
        {
            // The distance argument is the length of the step actually travelled this tick, not an
            // infinite ray. A blade that has not got there yet must not hit, or every throw would
            // land on the frame it was released.
            var from = new float3(0f, 1.2f, -5f);

            Assert.IsFalse(ShurikenSystem.SegmentsClose(from, new float3(0f, 0f, 1f), 2f,
                    Low, High, UnseenConfig.Default.Shuriken.HitRadiusMetres, out float _),
                "two metres of travel does not cross five metres of air");
        }
    }
}
