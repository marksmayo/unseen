using UnityEngine;

namespace Unseen.Client
{
    /// <summary>
    /// Bubbles rising off a body that is under the water.
    ///
    /// Driven by a replicated flag rather than by asking the water where it is. A proxy has a
    /// position and no idea what is around it, and having the client decide "is this point inside
    /// water" would be asking it to re-derive something the server already knows, on geometry it
    /// generated separately - two answers that agree until the day they do not.
    ///
    /// It is also a tell, not decoration. A ninja lying prone in the river channel is invisible and
    /// silent, and without something on the surface the only counter to hiding underwater would be
    /// knowing in advance to look. Bubbles mean the hiding place costs air *and* gives a little
    /// away, which is the trade the whole river is built on. The rate is deliberately low: enough
    /// to notice if you are watching the water, not enough to spot from across the town.
    /// </summary>
    public sealed class SubmergedBubbles : MonoBehaviour
    {
        [Tooltip("Bubbles per second while under. Low on purpose - this is a tell for somebody " +
                 "already looking at the water, not a marker over a hiding place.")]
        public float Rate = 6f;

        [Tooltip("How far up the body they leave from. Roughly the mouth.")]
        public float MouthHeight = 1.5f;

        [Tooltip("Metres per second they rise.")]
        public float RiseSpeed = 0.9f;

        private ParticleSystem _bubbles;
        private bool _under;

        /// <summary>Whether the body is currently under. Set from the snapshot flags.</summary>
        public bool Under
        {
            get => _under;
            set
            {
                if (_under == value) return;
                _under = value;

                if (_bubbles == null) Build();

                ParticleSystem.EmissionModule emission = _bubbles.emission;
                emission.enabled = _under;

                // Surfacing stops the stream but lets what is already in the water finish rising.
                // Clearing them would delete a column of bubbles mid-ascent, which reads as the
                // body having vanished rather than surfaced.
                if (_under) _bubbles.Play();
                else _bubbles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private void Build()
        {
            var host = new GameObject("Bubbles");
            host.transform.SetParent(transform, false);
            host.transform.localPosition = new Vector3(0f, MouthHeight, 0f);

            _bubbles = host.AddComponent<ParticleSystem>();
            _bubbles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = _bubbles.main;
            main.playOnAwake = false;
            main.loop = true;
            main.maxParticles = 48;
            main.startLifetime = 1.6f;
            main.startSize = 0.05f;
            main.startSpeed = RiseSpeed;

            // World space, so a body that swims away leaves its bubbles behind rather than towing
            // them. A trail of bubbles that follows somebody is a tracer, not a tell.
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            // Rising, not falling. Underwater the buoyancy is the whole behaviour.
            main.gravityModifier = -0.08f;

            ParticleSystem.EmissionModule emission = _bubbles.emission;
            emission.enabled = false;
            emission.rateOverTime = Rate;

            // A small sphere at the mouth rather than a point, so the stream wavers instead of
            // arriving as a ruler-straight line of identical dots.
            ParticleSystem.ShapeModule shape = _bubbles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.09f;

            ParticleSystem.ColorOverLifetimeModule colour = _bubbles.colorOverLifetime;
            colour.enabled = true;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.82f, 0.90f, 0.95f), 0f),
                        new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.7f, 0.15f),
                        new GradientAlphaKey(0f, 1f) });
            colour.color = gradient;

            var renderer = host.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // No shadows and no lighting: a bubble is a highlight, and forty-eight of them per
            // submerged body is not somewhere to spend a shadow pass.
            renderer.sharedMaterial = BubbleMaterial();

            // Their own layer's cull distance applies, so a bank of bubbles two hundred metres
            // away is not drawn at all. Nothing is lost - they are sub-pixel long before that.
            host.layer = Core.UnseenLayers.Decoration;
        }

        private static Material _material;

        private static Material BubbleMaterial()
        {
            if (_material != null) return _material;

            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            _material = new Material(shader) { name = "Bubbles" };
            _material.SetFloat("_Surface", 1f);
            _material.SetFloat("_Blend", 0f);
            _material.renderQueue = 3000;

            return _material;
        }
    }
}
