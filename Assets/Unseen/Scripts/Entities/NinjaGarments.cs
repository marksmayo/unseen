using System.Collections.Generic;
using UnityEngine;
using Unseen.Environment;

namespace Unseen.Entities
{
    /// <summary>
    /// Cloth on a ninja: the obi round the hips with its tails, the scarf at the neck, and the
    /// wraps on shins and forearms.
    ///
    /// The imported body is a single smooth skinned mesh, which reads as a mannequin - a figure
    /// with no edges, nothing that moves independently, and no silhouette of its own. Cloth is the
    /// cheapest thing that fixes all three at once: the wraps break the limbs into segments, and
    /// the hanging pieces give the figure an outline that changes as it moves, which is most of
    /// what makes a running body look like a person at forty metres.
    ///
    /// Everything here is decoration. Nothing carries a collider, nothing is replicated, and
    /// nothing is read by perception, physics or the parkour probes - a ninja with the garments
    /// stripped out plays identically to one wearing them.
    ///
    /// The hanging pieces are simulated rather than posed, because a stiff tail is worse than no
    /// tail: it reads as a plank bolted to the hips. The simulation is a chain of directions, each
    /// easing toward "hang down, and trail behind wherever the body just went". That is not cloth
    /// physics and does not pretend to be - it is three multiplies per link, it never explodes, and
    /// it costs nothing to run on sixty-four figures at once.
    /// </summary>
    public sealed class NinjaGarments : MonoBehaviour
    {
        /// <summary>
        /// One hanging piece: a chain of links, each parented to the one above it.
        ///
        /// Only the DIRECTIONS are stored. Positions come out of the transform hierarchy for free,
        /// which is what keeps this cheap and what stops the chain from ever coming apart.
        /// </summary>
        private struct Strand
        {
            public Transform[] Links;
            public Vector3[] Dir;
            public float Length;

            /// <summary>How hard the links want to hang straight down, against being dragged.</summary>
            public float Weight;

            /// <summary>How far the body's movement throws it about.</summary>
            public float Drag;

            /// <summary>How quickly it catches up. Lower is heavier cloth.</summary>
            public float Response;

            /// <summary>Idle drift, so a standing figure is not carved from stone.</summary>
            public float Sway;
        }

        private readonly List<Strand> _strands = new List<Strand>(4);

        private SkinnedMeshRenderer _body;
        private Transform _root;
        private Vector3 _lastRootPosition;
        private Vector3 _velocity;
        private bool _hasLast;
        private float _phase;

        [Tooltip("Metres per second of body movement that fully streams the cloth out behind.")]
        public float FullStreamSpeed = 6f;

        /// <summary>
        /// Cuts and hangs the cloth on an already-instantiated body.
        ///
        /// Called once at spawn, in whatever pose the rig starts in - which is the bind pose,
        /// standing straight, and the only pose in which "put a band round the shin" is
        /// unambiguous. Everything is placed in WORLD space and then parented to the bone, so it
        /// does not matter which axis the exporter chose to run each bone along.
        /// </summary>
        public static NinjaGarments Fit(AgentVisual visual, Material cloth, int id)
        {
            if (visual == null || cloth == null) return null;

            Transform root = visual.transform;
            var garments = visual.gameObject.AddComponent<NinjaGarments>();

            garments._body = visual.Body;
            garments._root = root;

            // A per-agent phase, so a crowd of ninja does not sway in unison like a chorus line.
            garments._phase = (Mathf.Abs(id) % 97) * 0.64f;

            Material accent = Accent(cloth, id);

            Transform hips = Bone(root, "Hips");
            Transform neck = Bone(root, "Neck");
            Transform head = Bone(root, "Head");

            garments.FitObi(hips, accent, id);
            garments.FitScarf(neck, head, accent);
            garments.FitWrap(Bone(root, "LeftLeg"), Bone(root, "LeftFoot"), accent, 0.075f, 3);
            garments.FitWrap(Bone(root, "RightLeg"), Bone(root, "RightFoot"), accent, 0.075f, 3);
            garments.FitWrap(Bone(root, "LeftForeArm"), Bone(root, "LeftHand"), accent, 0.055f, 2);
            garments.FitWrap(Bone(root, "RightForeArm"), Bone(root, "RightHand"), accent, 0.055f, 2);

            return garments;
        }

        // ------------------------------------------------------------------ the pieces

        /// <summary>The obi: a wide band round the hips, a knot at the back, and two tails.</summary>
        private void FitObi(Transform hips, Material cloth, int id)
        {
            if (hips == null) return;

            Vector3 up = _root.up;
            Vector3 back = -_root.forward;
            Vector3 centre = hips.position;

            Band(hips, cloth, centre - up * 0.045f, up, 0.34f, 0.13f, 0.26f);

            // The knot, off to one side rather than dead centre. Perfectly centred it reads as a
            // machined fitting; a hand's width across it reads as tied.
            float side = (id & 1) == 0 ? 0.05f : -0.05f;
            Blob(hips, cloth, centre + back * 0.12f + _root.right * side + up * 0.01f, 0.11f);

            // Two tails, one either side of the knot and of different lengths - a pair of matched
            // tails is a ribbon on a gift, not a belt somebody tied in the dark.
            Hang(hips, cloth, centre + back * 0.13f + _root.right * (side - 0.045f), back,
                links: 3, length: 0.17f, width: 0.075f,
                weight: 1f, drag: 0.85f, response: 9f, sway: 0.055f);

            Hang(hips, cloth, centre + back * 0.13f + _root.right * (side + 0.045f), back,
                links: 3, length: 0.14f, width: 0.065f,
                weight: 1f, drag: 0.95f, response: 11f, sway: 0.07f);
        }

        /// <summary>The scarf: a collar, and a long tail that streams when the body is moving.</summary>
        private void FitScarf(Transform neck, Transform head, Material cloth)
        {
            Transform anchor = neck != null ? neck : head;
            if (anchor == null) return;

            Vector3 up = _root.up;
            Vector3 back = -_root.forward;
            Vector3 collar = anchor.position;

            Band(anchor, cloth, collar - up * 0.03f, up, 0.19f, 0.10f, 0.19f);

            // Lighter than the obi tails and much longer: this is the piece that says "running"
            // from across a courtyard, so it is given the most drag and the least weight.
            Hang(anchor, cloth, collar + back * 0.07f, back,
                links: 4, length: 0.19f, width: 0.10f,
                weight: 0.55f, drag: 1.6f, response: 7f, sway: 0.1f);
        }

        /// <summary>
        /// Wraps up a limb: bands round the bone, spaced along it.
        ///
        /// The axis comes from the bone to its child rather than from any local axis, because
        /// which way a bone points in its own frame is an exporter's choice and not something to
        /// hard-code against.
        /// </summary>
        private void FitWrap(Transform bone, Transform child, Material cloth, float radius, int bands)
        {
            if (bone == null || child == null) return;

            Vector3 span = child.position - bone.position;
            float length = span.magnitude;
            if (length < 0.02f) return;

            Vector3 axis = span / length;

            for (int i = 0; i < bands; i++)
            {
                // Spread over the middle two thirds. A band right on a joint slides through the
                // limb when the joint bends.
                float t = 0.18f + 0.64f * (bands == 1 ? 0.5f : i / (float)(bands - 1));

                // Each band a little narrower than the last and slightly tilted, so it reads as
                // one strip spiralling up rather than a stack of identical rings.
                float width = radius * (2.16f - i * 0.06f);
                float tilt = (i % 2 == 0 ? 5f : -5f);

                Transform band = Band(bone, cloth, bone.position + axis * (length * t), axis,
                    width, radius * 0.62f, width * 0.86f);

                if (band != null) band.Rotate(tilt, 0f, tilt * 0.6f, Space.Self);
            }
        }

        // ------------------------------------------------------------------ primitives

        /// <summary>
        /// A band round a limb or a waist. Placed in world space at <paramref name="centre"/> with
        /// its axis along <paramref name="axis"/>, then parented to the bone so it rides along.
        /// </summary>
        private Transform Band(Transform bone, Material cloth, Vector3 centre, Vector3 axis,
            float width, float height, float depth)
        {
            var piece = new GameObject("Wrap");
            Transform t = piece.transform;

            t.position = centre - axis * (height * 0.5f);
            t.rotation = Quaternion.LookRotation(Perpendicular(axis), axis);
            t.localScale = new Vector3(width, height, depth);

            Render(piece, OrganicMeshFactory.Tube(8, 2, 0.94f, 0f, 0.12f), cloth);

            // World position kept: the band was measured in world space against the standing pose.
            t.SetParent(bone, true);
            Unscale(t, new Vector3(width, height, depth));
            return t;
        }

        /// <summary>A knot. Small, lumpy, and not worth more geometry than that.</summary>
        private void Blob(Transform bone, Material cloth, Vector3 centre, float size)
        {
            var piece = new GameObject("Knot");
            Transform t = piece.transform;

            t.position = centre;
            t.rotation = _root.rotation;

            var scale = new Vector3(size, size * 0.72f, size * 0.8f);
            t.localScale = scale;

            Render(piece, OrganicMeshFactory.Blob(3, 6, 0.4f, 2), cloth);

            t.SetParent(bone, true);
            Unscale(t, scale);
        }

        /// <summary>
        /// A hanging piece: a chain of links, each parented to the one above, registered for
        /// simulation.
        /// </summary>
        private void Hang(Transform bone, Material cloth, Vector3 from, Vector3 back, int links,
            float length, float width, float weight, float drag, float response, float sway)
        {
            var strand = new Strand
            {
                Links = new Transform[links],
                Dir = new Vector3[links],
                Length = length,
                Weight = weight,
                Drag = drag,
                Response = response,
                Sway = sway
            };

            // Resting a little back rather than straight down, which is where a tied sash sits and
            // stops the first frame from looking like it fell off.
            Vector3 rest = (Vector3.down * 3f + back).normalized;

            Transform parent = bone;

            for (int i = 0; i < links; i++)
            {
                var piece = new GameObject($"Tail_{i}");
                Transform t = piece.transform;

                t.position = i == 0 ? from : strand.Links[i - 1].position + rest * length;
                t.rotation = Quaternion.LookRotation(Perpendicular(rest), -rest);

                var visual = new GameObject("Cloth");
                visual.transform.SetParent(t, false);

                // The tube runs from its origin up +Y, and a tail runs DOWN from its link, so the
                // visual is turned over rather than the mesh being rebuilt mirrored.
                visual.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
                visual.transform.localScale = new Vector3(width, length, width * 0.26f);

                // Narrowing as it falls, because a sash is cut that way and because a tail that
                // keeps its full width to the tip reads as a strap.
                Render(visual, OrganicMeshFactory.Tube(5, 2, 0.78f, 0f, 0.16f), cloth);

                t.SetParent(parent, true);
                Unscale(t, Vector3.one);

                strand.Links[i] = t;
                strand.Dir[i] = rest;
                parent = t;
            }

            _strands.Add(strand);
        }

        // ------------------------------------------------------------------ simulation

        private void LateUpdate()
        {
            if (_root == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 position = _root.position;

            if (_hasLast)
            {
                // Eased rather than taken raw: a single frame's delta on a networked proxy is
                // noise, and cloth driven by noise shivers.
                Vector3 instant = (position - _lastRootPosition) / dt;
                _velocity = Vector3.Lerp(_velocity, instant, 1f - Mathf.Exp(-9f * dt));
            }

            _lastRootPosition = position;
            _hasLast = true;

            // Off-screen figures keep their cloth where it was. Nobody can see it, and sixty-four
            // chains of four links each is not free.
            if (_body != null && !_body.isVisible) return;

            Step(dt, _velocity, Time.time);
        }

        /// <summary>
        /// One step of the cloth, given how fast the body is moving.
        ///
        /// Separate from LateUpdate and given its own clock, because Unity runs no lifecycle
        /// callbacks in edit mode: cloth that could only be advanced by the game loop could only
        /// be checked by playing the game and looking at it, which is not a test.
        /// </summary>
        public void Step(float dt, Vector3 velocity, float time)
        {
            if (_strands.Count == 0 || dt <= 0f) return;

            // How hard the body is being thrown about, as a fraction. Clamped, so a teleport or a
            // respawn cannot fling the cloth into orbit.
            Vector3 flow = -velocity;
            float speed = flow.magnitude;
            if (speed > 0.001f) flow /= speed;

            float stream = Mathf.Clamp01(speed / Mathf.Max(0.5f, FullStreamSpeed));
            time += _phase;

            for (int s = 0; s < _strands.Count; s++)
            {
                Strand strand = _strands[s];

                for (int i = 0; i < strand.Links.Length; i++)
                {
                    Transform link = strand.Links[i];
                    if (link == null) continue;

                    // Hang down, trail behind, and drift. Links further from the anchor swing
                    // wider, which is the whole difference between cloth and a hinge.
                    float reach = 1f + i * 0.45f;

                    Vector3 desired = Vector3.down * strand.Weight
                                      + flow * (stream * strand.Drag * reach);

                    desired.x += Mathf.Sin(time * 1.7f + i * 0.9f) * strand.Sway * reach;
                    desired.z += Mathf.Cos(time * 1.3f + i * 1.4f) * strand.Sway * reach;

                    if (desired.sqrMagnitude < 0.0001f) desired = Vector3.down;
                    desired.Normalize();

                    // Each link is pulled toward the one above it as well, so the chain bends in a
                    // curve instead of kinking at every joint.
                    if (i > 0) desired = Vector3.Slerp(desired, strand.Dir[i - 1], 0.35f).normalized;

                    float blend = 1f - Mathf.Exp(-strand.Response * dt);
                    Vector3 dir = Vector3.Slerp(strand.Dir[i], desired, blend).normalized;

                    strand.Dir[i] = dir;
                    link.rotation = Quaternion.LookRotation(Perpendicular(-dir), -dir);

                    if (i > 0) link.localPosition = new Vector3(0f, -strand.Length, 0f);
                }

                _strands[s] = strand;
            }
        }

        /// <summary>Hanging pieces on this body. For the tests; the game does not need it.</summary>
        public int StrandCount => _strands.Count;

        /// <summary>
        /// Which way one hanging piece is pointing at its tip, in world space.
        ///
        /// The single number that says whether the cloth is behaving: straight down when standing,
        /// swung out behind when running, and somewhere between the two while it settles.
        /// </summary>
        public Vector3 TipDirection(int strand)
        {
            if (strand < 0 || strand >= _strands.Count) return Vector3.down;

            Vector3[] dir = _strands[strand].Dir;
            return dir.Length == 0 ? Vector3.down : dir[dir.Length - 1];
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Cancels the bone's scale so a piece measured in metres stays that size.
        ///
        /// Rigs arrive with scale baked into the bones often enough that placing a 13 cm band on a
        /// bone scaled by a hundred produces a thirteen metre band, and the failure is not subtle.
        /// </summary>
        private static void Unscale(Transform piece, Vector3 metres)
        {
            Transform parent = piece.parent;
            if (parent == null) { piece.localScale = metres; return; }

            Vector3 s = parent.lossyScale;
            piece.localScale = new Vector3(
                metres.x / (Mathf.Abs(s.x) < 0.0001f ? 1f : s.x),
                metres.y / (Mathf.Abs(s.y) < 0.0001f ? 1f : s.y),
                metres.z / (Mathf.Abs(s.z) < 0.0001f ? 1f : s.z));
        }

        /// <summary>Any direction square to this one, chosen so it never degenerates.</summary>
        private static Vector3 Perpendicular(Vector3 axis)
        {
            Vector3 reference = Mathf.Abs(axis.y) > 0.95f ? Vector3.forward : Vector3.up;
            Vector3 side = Vector3.Cross(axis, reference);
            return side.sqrMagnitude < 0.0001f ? Vector3.right : side.normalized;
        }

        private static void Render(GameObject on, Mesh mesh, Material material)
        {
            on.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = on.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            // No shadows from a shin wrap. Sixty-four figures with a dozen pieces each is nearly
            // eight hundred extra shadow casters for detail nobody would ever notice missing.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>
        /// A bone by name, anywhere under the body.
        ///
        /// By name rather than through the humanoid avatar, because half these bones - the shin,
        /// the forearm - are only reachable through the avatar if the rig was mapped, and a body
        /// that lost its wraps because an import setting changed is a worse failure than one that
        /// looked up a string.
        /// </summary>
        private static Transform Bone(Transform root, string name)
        {
            var all = root.GetComponentsInChildren<Transform>(true);

            for (int i = 0; i < all.Length; i++)
                if (all[i].name == name) return all[i];

            return null;
        }

        // ------------------------------------------------------------------ colour

        private static readonly Dictionary<int, Material> Accents = new Dictionary<int, Material>(8);

        /// <summary>
        /// The colours a ninja's cloth comes in.
        ///
        /// All of them dark. The garments exist to give the figure a silhouette, not to make it
        /// easier to spot - a bright sash would hand away a hiding place that the whole stealth
        /// model is built on, and would be the one piece of art in the game that changed how it
        /// plays.
        /// </summary>
        private static readonly Color[] Palette =
        {
            new Color(0.13f, 0.14f, 0.19f),  // indigo, near black
            new Color(0.21f, 0.10f, 0.11f),  // oxblood
            new Color(0.15f, 0.16f, 0.13f),  // moss
            new Color(0.19f, 0.17f, 0.14f)   // dust
        };

        /// <summary>
        /// One shared material per colour, not one per agent.
        ///
        /// Sixty-four agents with their own material instances is sixty-four batches that the SRP
        /// batcher cannot merge, for four distinct appearances.
        /// </summary>
        private static Material Accent(Material template, int id)
        {
            int index = Mathf.Abs(id) % Palette.Length;
            if (Accents.TryGetValue(index, out Material cached) && cached != null) return cached;

            var material = new Material(template) { name = $"NinjaCloth_{index}" };

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Palette[index]);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Palette[index]);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.12f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);

            Accents[index] = material;
            return material;
        }
    }
}
