using System.Collections.Generic;
using UnityEngine;
using Unseen.Audio;
using Unseen.Core;
using Unseen.Items;
using Unseen.Perception;

namespace Unseen.Environment
{
    /// <summary>
    /// Builds the greybox castle town: a grid of paper-walled compounds with walkable roofs and
    /// interior rafters, streets between them, a keep in the middle, and a sewer level underneath.
    ///
    /// It is deliberately procedural and deterministic from a seed. That gives the netcode and AI
    /// work a real multi-level playground - roofs, interiors, verticality, light and darkness -
    /// months before any of it is authored by hand, and it regenerates identically on server and
    /// client so the destructible ids line up.
    /// </summary>
    public sealed class GreyboxTownGenerator : MonoBehaviour
    {
        [Header("Layout")]
        public int Seed = 20260824;

        [Tooltip("Compounds per side. 16 gives a 16x16 town, about ten times the area of the " +
                 "original 5x5. Drop it back if generation or frame time becomes a problem.")]
        [Range(1, 24)] public int GridSize = 16;

        public float BlockSize = 34f;
        public float StreetWidth = 12f;
        public float WallHeight = 4.2f;
        public float SecondStoreyHeight = 3.6f;

        [Tooltip("How far the roof overhangs the walls. Eaves are the rooftop traversal route.")]
        public float EaveOverhang = 1.4f;

        [Header("River")]
        [Tooltip("Runs a river down one column of the grid, with a bridge at every street.")]
        public bool BuildRiver = true;

        [Tooltip("Depth of the channel below street level. Deep enough to stand under a bridge.")]
        public float RiverDepth = 4.2f;

        [Tooltip("Width of the water itself. The towpaths sit either side of it.")]
        public float RiverWidth = 16f;

        [Header("Pagodas")]
        [Tooltip("Fraction of blocks given over to a climbable pagoda instead of a compound.")]
        [Range(0f, 0.5f)] public float PagodaChance = 0.09f;

        [Tooltip("Storeys in a pagoda, before the finial.")]
        [Range(2, 8)] public int PagodaStoreys = 5;

        [Header("Sewers")]
        public bool BuildSewers = true;
        public float SewerDepth = 9f;
        public float SewerCorridorWidth = 5f;
        public float SewerHeight = 3.2f;

        [Header("Content density")]
        [Range(0f, 1f)] public float TwoStoreyChance = 0.45f;
        [Range(0f, 4f)] public float LanternsPerCompound = 2.5f;
        [Range(0f, 4f)] public float ContainersPerCompound = 1.6f;

        [Header("Lighting")]
        [Tooltip("Moonlight brightness. A stealth game still has to be legible: darkness should " +
                 "mean 'hard to be seen', not 'cannot see'.")]
        public float MoonlightIntensity = 0.85f;

        [Tooltip("Moonlight elevation and heading, degrees.")]
        public Vector2 MoonlightAngles = new Vector2(38f, 145f);

        [Tooltip("Multiplier on lantern brightness. Visual only - the stealth index reads " +
                 "StealthLightSource.Intensity, which is deliberately separate.")]
        public float LanternVisualIntensity = 34f;

        [Header("Art")]
        [Tooltip("Textured materials. Falls back to Resources, then to flat greybox colours.")]
        public GreyboxMaterialSet MaterialSet;

        [Header("Loot")]
        [Tooltip("Loot table used by generated containers. One is created in code when empty.")]
        public LootTable Table;

        private System.Random _random;
        private Transform _root;
        private Material _stone;
        private Material _timber;
        private Material _paper;
        private Material _tile;
        private Material _rafter;
        private Material _woodFloor;
        private Material _tatami;
        private Material _ground;
        private float _textureMetres = 2.5f;

        [Tooltip("World metres per texture repeat on roof tiers. Finer than the rest of the town.")]
        public float RoofTextureMetres = 0.9f;
        private float _rampartRing;
        private int _riverColumn = -1;
        private float _riverCentreX = float.NaN;
        private int _pagodas;
        private int _trees;
        private int _shrubs;
        private bool _textured;
        private GreyboxMaterialSet _set;
        private Material _lanternGlow;
        private Material _plaster;
        private Material _darkTimber;
        private Material _water;
        private Material _foliage;
        private Material _shojiPaper;
        private LootTable _runtimeTable;

        /// <summary>
        /// Builds the town and returns its descriptor. Generation is always explicit - nothing runs
        /// from Awake - so a hand-authored scene is never overwritten by accident.
        /// </summary>
        public MapDescriptor Generate()
        {
            _random = new System.Random(Seed);
            CreateMaterials();

            _root = new GameObject("CastleTown").transform;
            _root.SetParent(transform, false);

            float pitch = BlockSize + StreetWidth;
            float extent = pitch * GridSize * 0.5f;

            // One column of the grid is water rather than buildings. Chosen before the ground is
            // laid, because the ground has to be built with a gap for it: a single slab across the
            // whole map would simply roof the channel over.
            _riverColumn = BuildRiver && GridSize >= 5 ? GridSize / 2 - 2 : -1;
            _riverCentreX = _riverColumn >= 0
                ? (_riverColumn - (GridSize - 1) * 0.5f) * pitch
                : float.NaN;

            BuildGround(extent, pitch);
            BuildMoonlight();

            for (int gx = 0; gx < GridSize; gx++)
            for (int gz = 0; gz < GridSize; gz++)
            {
                if (gx == _riverColumn) continue;

                bool isCentre = gx == GridSize / 2 && gz == GridSize / 2;
                var origin = new Vector3(
                    (gx - (GridSize - 1) * 0.5f) * pitch,
                    0f,
                    (gz - (GridSize - 1) * 0.5f) * pitch);

                if (isCentre) BuildKeep(origin);
                else if (_random.NextDouble() < PagodaChance) BuildPagoda(origin, gx * 31 + gz);
                else BuildCompound(origin, gx * 31 + gz);
            }

            if (_riverColumn >= 0) BuildRiverChannel(extent, pitch);
            if (BuildSewers) BuildSewerNetwork(extent, pitch);
            BuildStreetLanterns(extent, pitch);
            BuildFoliage(extent, pitch);
            BuildRampart(extent);
            BudgetLanternLights();
            CombineStatics();

            MapDescriptor descriptor = gameObject.GetComponent<MapDescriptor>();
            if (descriptor == null) descriptor = gameObject.AddComponent<MapDescriptor>();
            descriptor.Center = Vector3.zero;
            // The playable radius is the rampart, not an estimate from the grid: the mist, the bot
            // patrol picker and the bounds clamp all read this, and they should all agree with the
            // wall the player can actually see.
            descriptor.Radius = _rampartRing > 0f ? _rampartRing + 1f : extent * 1.15f;
            descriptor.FloorY = -SewerDepth - 2f;
            descriptor.CeilingY = WallHeight + SecondStoreyHeight + 12f;

            Debug.Log($"[Unseen] greybox town generated: {ShojiPanel.All.Count} shoji, " +
                      $"{Lantern.All.Count} lanterns, {LootContainer.All.Count} containers, " +
                      $"radius {descriptor.Radius:0} m, " +
                      $"{(_textured ? "textured" : "flat greybox")}, " +
                      $"{BoxMeshFactory.CachedMeshCount} box meshes, " +
                      $"{_root.GetComponentsInChildren<Renderer>(true).Length} renderers, " +
                      $"{_root.GetComponentsInChildren<Collider>(true).Length} colliders, " +
                      $"{_pagodas} pagodas, {_trees} trees, {_shrubs} shrubs, " +
                      $"river={(_riverColumn >= 0 ? "yes" : "no")}");

            return descriptor;
        }

        // ---------------------------------------------------------------- pieces

        /// <summary>
        /// The ground the town stands on, laid as one slab or as two with the river channel
        /// between them.
        /// </summary>
        private void BuildGround(float extent, float pitch)
        {
            float span = extent * 2.4f;

            if (_riverColumn < 0)
            {
                Transform whole = Box(_root, "Ground", new Vector3(0f, -0.5f, 0f),
                    new Vector3(span, 1f, span), UnseenLayers.Default, _ground);
                Acoustics(whole, attenuation: 0.9f, footstep: 1.15f, radius: 1.2f);
                return;
            }

            float channelHalf = pitch * 0.5f;
            float left = _riverCentreX - channelHalf;
            float right = _riverCentreX + channelHalf;

            // West bank and east bank. Widths differ because the river is not in the middle.
            float westWidth = left + span * 0.5f;
            float eastWidth = span * 0.5f - right;

            if (westWidth > 1f)
            {
                Transform west = Box(_root, "Ground_West",
                    new Vector3(left - westWidth * 0.5f, -0.5f, 0f),
                    new Vector3(westWidth, 1f, span), UnseenLayers.Default, _ground);
                Acoustics(west, attenuation: 0.9f, footstep: 1.15f, radius: 1.2f);
            }

            if (eastWidth > 1f)
            {
                Transform east = Box(_root, "Ground_East",
                    new Vector3(right + eastWidth * 0.5f, -0.5f, 0f),
                    new Vector3(eastWidth, 1f, span), UnseenLayers.Default, _ground);
                Acoustics(east, attenuation: 0.9f, footstep: 1.15f, radius: 1.2f);
            }

            // Caps beyond the ends of the channel, so the river does not run off the map edge into
            // a hole a player can fall down.
            float capDepth = span * 0.5f - extent;
            if (capDepth <= 1f) return;

            for (int end = -1; end <= 1; end += 2)
            {
                Transform cap = Box(_root, $"Ground_Cap_{end}",
                    new Vector3(_riverCentreX, -0.5f, end * (extent + capDepth * 0.5f)),
                    new Vector3(pitch, 1f, capDepth), UnseenLayers.Default, _ground);
                Acoustics(cap, attenuation: 0.9f, footstep: 1.15f, radius: 1.2f);
            }
        }

        private void BuildMoonlight()
        {
            ApplySky();

            var lightHost = new GameObject("Moonlight");
            lightHost.transform.SetParent(_root, false);
            lightHost.transform.rotation = Quaternion.Euler(MoonlightAngles.x, MoonlightAngles.y, 0f);

            Light light = lightHost.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.62f, 0.72f, 1f);
            light.intensity = MoonlightIntensity;
            light.shadows = LightShadows.Soft;
        }

        /// <summary>
        /// Applies the night sky at runtime rather than relying on the scene's saved lighting, so a
        /// procedurally built town looks right wherever it is generated - play mode, a build, or a tool.
        /// </summary>
        private void ApplySky()
        {
            if (_set == null || _set.Sky == null) return;

            // RenderSettings lives in UnityEngine; only AmbientMode is in UnityEngine.Rendering.
            RenderSettings.skybox = _set.Sky;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            RenderSettings.ambientIntensity = _set.AmbientIntensity;
            RenderSettings.fog = _set.FogDensity > 0f;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = _set.FogDensity;
            RenderSettings.fogColor = _set.FogColor;
        }

        /// <summary>One walled compound: outer wall, paper-divided interior, walkable roof, rafters.</summary>
        private void BuildCompound(Vector3 origin, int salt)
        {
            var compound = new GameObject($"Compound_{salt}").transform;
            compound.SetParent(_root, false);
            compound.localPosition = origin;

            float half = BlockSize * 0.5f;
            bool twoStorey = _random.NextDouble() < TwoStoreyChance;
            float height = twoStorey ? WallHeight + SecondStoreyHeight : WallHeight;

            // Outer walls with a doorway gap on one random side.
            int doorSide = _random.Next(4);
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                Vector3 centre = horizontal
                    ? new Vector3(0f, height * 0.5f, half * sign)
                    : new Vector3(half * sign, height * 0.5f, 0f);
                Vector3 size = horizontal
                    ? new Vector3(BlockSize, height, 0.4f)
                    : new Vector3(0.4f, height, BlockSize);

                if (side == doorSide)
                {
                    // Split the wall to leave a 3 m doorway in the middle.
                    float segment = (BlockSize - 3f) * 0.5f;
                    for (int s = -1; s <= 1; s += 2)
                    {
                        Vector3 offset = horizontal
                            ? new Vector3(s * (segment + 3f) * 0.5f, 0f, 0f)
                            : new Vector3(0f, 0f, s * (segment + 3f) * 0.5f);
                        Vector3 segmentSize = horizontal
                            ? new Vector3(segment, height, 0.4f)
                            : new Vector3(0.4f, height, segment);

                        Transform wall = Box(compound, $"Wall_{side}_{s}", centre + offset, segmentSize,
                            UnseenLayers.Occluder, _plaster);
                        Acoustics(wall, 0.75f, 1f, 1f);
                    }

                    DressDoorway(compound, horizontal, sign, half, height);
                    DressWall(compound, side, horizontal, sign, half, height, true);
                    continue;
                }

                Transform solid = Box(compound, $"Wall_{side}", centre, size, UnseenLayers.Occluder, _plaster);
                Acoustics(solid, 0.75f, 1f, 1f);
                DressWall(compound, side, horizontal, sign, half, height, false);
            }

            // Interior: tatami floor plus a paper cross that divides four rooms.
            Transform floor = Box(compound, "Floor", new Vector3(0f, 0.05f, 0f),
                new Vector3(BlockSize - 1f, 0.1f, BlockSize - 1f), UnseenLayers.Default, _tatami);
            Acoustics(floor, 0.6f, 0.55f, 0.6f);

            BuildShojiRun(compound, new Vector3(0f, WallHeight * 0.5f, 0f), BlockSize - 2f, true, WallHeight);
            BuildShojiRun(compound, new Vector3(0f, WallHeight * 0.5f, 0f), BlockSize - 2f, false, WallHeight);

            // Rafters under the roof: the classic overhead ambush lane.
            int rafters = 3;
            for (int i = 0; i < rafters; i++)
            {
                float t = (i + 1f) / (rafters + 1f);
                float z = Mathf.Lerp(-half + 2f, half - 2f, t);
                Transform beam = Box(compound, $"Rafter_{i}",
                    new Vector3(0f, height - 0.6f, z),
                    new Vector3(BlockSize - 1.5f, 0.3f, 0.5f),
                    UnseenLayers.Rafter, _rafter);
                Acoustics(beam, 0.3f, 0.4f, 0.5f);
            }

            float roofTop = BuildHipRoof(compound, BlockSize + EaveOverhang * 2f, height);

            Transform ridge = Box(compound, "Ridge",
                new Vector3(0f, roofTop + 0.5f, 0f),
                new Vector3(BlockSize * 0.6f, 1f, 1.2f),
                UnseenLayers.GrappleAnchor, _tile);
            Acoustics(ridge, 0.5f, 1.2f, 1.3f);

            if (twoStorey)
            {
                Transform midFloor = Box(compound, "UpperFloor",
                    new Vector3(0f, WallHeight, 0f),
                    new Vector3(BlockSize - 3f, 0.3f, BlockSize - 3f),
                    UnseenLayers.Default, _woodFloor);
                Acoustics(midFloor, 0.65f, 0.7f, 0.8f);
            }

            PlaceLanterns(compound, half, height);
            PlaceContainers(compound, half);
        }

        /// <summary>
        /// The outer rampart: a stone wall with a walkable wall-walk and a parapet, ringing the
        /// town, plus an invisible barrier above it.
        ///
        /// The wall is the honest answer to "where does the map end" - a castle town has one, and
        /// it gives the edge of the world a reason to exist rather than an invisible stop. The
        /// barrier above it exists because the wall is climbable and grappleable like everything
        /// else, and a rampart you can simply vault over is not a boundary.
        /// <see cref="Unseen.BattleRoyale.WorldBoundsSystem"/> is the authoritative backstop
        /// behind both.
        /// </summary>
        private void BuildRampart(float extent)
        {
            var rampart = new GameObject("Rampart").transform;
            rampart.SetParent(_root, false);

            float ring = extent + StreetWidth * 0.5f;
            _rampartRing = ring;
            float span = ring * 2f + 4f;
            const float bankHeight = 5.4f;
            const float walkWidth = 4f;
            const float parapetHeight = 1.6f;

            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;

                Vector3 Place(float y, float outward) => horizontal
                    ? new Vector3(0f, y, ring * sign + outward * sign)
                    : new Vector3(ring * sign + outward * sign, y, 0f);

                Vector3 Size(float y, float depth) => horizontal
                    ? new Vector3(span, y, depth)
                    : new Vector3(depth, y, span);

                // The bank itself, walkable along the top like any other roof.
                Transform bank = Box(rampart, $"Rampart_{side}", Place(bankHeight * 0.5f, 0f),
                    Size(bankHeight, walkWidth), UnseenLayers.Occluder, _stone);
                Acoustics(bank, 0.95f, 1.2f, 1.2f);

                // Parapet on the outer lip, so the wall-walk feels like one.
                Transform parapet = Box(rampart, $"Parapet_{side}",
                    Place(bankHeight + parapetHeight * 0.5f, walkWidth * 0.5f - 0.3f),
                    Size(parapetHeight, 0.6f), UnseenLayers.Occluder, _stone);
                Acoustics(parapet, 0.95f, 1.2f, 1.2f);

                Detail(rampart, $"ParapetCap_{side}",
                    Place(bankHeight + parapetHeight + 0.1f, walkWidth * 0.5f - 0.3f),
                    Size(0.2f, 0.8f), _darkTimber);

                // Invisible barrier. Tall enough that the grapple, which reaches 34 m, cannot find
                // an anchor over the top of it from anywhere inside the map.
                var barrier = new GameObject($"Barrier_{side}");
                barrier.transform.SetParent(rampart, false);
                barrier.transform.localPosition = Place(40f, walkWidth * 0.5f + 0.4f);
                barrier.layer = UnseenLayers.Default;

                var block = barrier.AddComponent<BoxCollider>();
                block.size = Size(90f, 0.8f);
                barrier.isStatic = true;
            }
        }

        // ---------------------------------------------------------------- river

        /// <summary>
        /// The river: a sunken channel down one column of the grid, with a towpath either side and
        /// a bridge at every street crossing.
        ///
        /// The channel is deliberately deeper than a ninja is tall. That is what makes the towpath
        /// worth using - down there you are below every sightline in the town, and the space under
        /// a bridge deck is the darkest cover on the map.
        /// </summary>
        private void BuildRiverChannel(float extent, float pitch)
        {
            var river = new GameObject("River").transform;
            river.SetParent(_root, false);

            float length = extent * 2f + StreetWidth;
            float channelHalf = (BlockSize + StreetWidth) * 0.5f;
            float towpath = channelHalf - RiverWidth * 0.5f;
            float bedY = -RiverDepth;

            Transform bed = Box(river, "Bed", new Vector3(_riverCentreX, bedY - 0.3f, 0f),
                new Vector3(RiverWidth, 0.6f, length), UnseenLayers.Default, _stone);
            Acoustics(bed, 0.9f, 1.3f, 1.4f);

            // Shallow water: walkable, loud, and the worst place to cross unseen.
            Transform water = Box(river, "Water", new Vector3(_riverCentreX, bedY + 0.12f, 0f),
                new Vector3(RiverWidth, 0.24f, length), UnseenLayers.Default, _water);
            Acoustics(water, 0.2f, 2.2f, 2.4f);

            for (int side = -1; side <= 1; side += 2)
            {
                float pathCentre = _riverCentreX + side * (RiverWidth * 0.5f + towpath * 0.5f);

                Transform path = Box(river, $"Towpath_{side}",
                    new Vector3(pathCentre, bedY + 0.35f, 0f),
                    new Vector3(towpath, 0.7f, length), UnseenLayers.Default, _stone);
                Acoustics(path, 0.85f, 1.1f, 1.15f);

                float wallX = _riverCentreX + side * channelHalf;
                Transform wall = Box(river, $"Embankment_{side}",
                    new Vector3(wallX, bedY * 0.5f, 0f),
                    new Vector3(0.8f, RiverDepth + 0.4f, length), UnseenLayers.Occluder, _stone);
                Acoustics(wall, 0.95f, 1f, 1f);
            }

            BuildRiverStairs(river, extent, pitch, channelHalf, bedY);
            BuildBridges(river, extent, pitch, channelHalf, bedY);
        }

        /// <summary>Steps down to the towpath, so the river is a route rather than a trap.</summary>
        private void BuildRiverStairs(Transform river, float extent, float pitch,
            float channelHalf, float bedY)
        {
            int crossings = Mathf.Max(1, Mathf.RoundToInt(extent * 2f / pitch));

            for (int i = 0; i <= crossings; i++)
            {
                float z = Mathf.Lerp(-extent, extent, i / (float)crossings) + pitch * 0.5f;
                if (Mathf.Abs(z) > extent) continue;

                for (int side = -1; side <= 1; side += 2)
                {
                    float x = _riverCentreX + side * (channelHalf - 1.4f);
                    const int steps = 9;

                    for (int stepIndex = 0; stepIndex < steps; stepIndex++)
                    {
                        float t = (stepIndex + 1f) / steps;

                        // Bottom step lands ON the towpath, not inside it. Ending the run at the
                        // towpath's centre height buried the last three steps in the slab, and the
                        // wall-intrusion sampler duly found bots standing inside geometry there.
                        float top = Mathf.Lerp(0.1f, bedY + 1.2f, t);
                        const float depth = 0.7f;

                        // Each step is a solid block down to the bed rather than a floating slab.
                        // Thin treads left a hollow underneath, and bots walking the flight kept
                        // ending up inside it - 444 embedded samples in the first run of this.
                        float bottom = bedY - 0.3f;
                        float thickness = Mathf.Max(0.4f, top - bottom);

                        Transform step = Box(river, $"Stair_{i}_{side}_{stepIndex}",
                            new Vector3(x, top - thickness * 0.5f, z + stepIndex * depth * -side),
                            new Vector3(2.4f, thickness, depth), UnseenLayers.Default, _stone);
                        Acoustics(step, 0.9f, 1.1f, 1.1f);
                    }
                }
            }
        }

        /// <summary>
        /// A bridge at every street that meets the river. Deck, piers, railings and a lantern on
        /// each parapet post - and headroom beneath, which is the point.
        /// </summary>
        private void BuildBridges(Transform river, float extent, float pitch,
            float channelHalf, float bedY)
        {
            int crossings = Mathf.Max(1, Mathf.RoundToInt(extent * 2f / pitch));

            for (int i = 0; i <= crossings; i++)
            {
                float z = Mathf.Lerp(-extent, extent, i / (float)crossings);
                if (Mathf.Abs(z) > extent - 1f) continue;

                var bridge = new GameObject($"Bridge_{i}").transform;
                bridge.SetParent(river, false);

                float span = channelHalf * 2f + 3f;
                const float deckWidth = 9f;
                const float deckY = 0.55f;

                Transform deck = Box(bridge, "Deck", new Vector3(_riverCentreX, deckY, z),
                    new Vector3(span, 0.5f, deckWidth), UnseenLayers.Default, _woodFloor);
                Acoustics(deck, 0.55f, 1.4f, 1.5f);

                // Piers, standing in the water. They also break the sightline along the channel.
                for (int p = -1; p <= 1; p += 2)
                {
                    Transform pier = Box(bridge, $"Pier_{p}",
                        new Vector3(_riverCentreX + p * RiverWidth * 0.28f, bedY * 0.5f + 0.2f, z),
                        new Vector3(1.1f, RiverDepth, 1.4f), UnseenLayers.Occluder, _stone);
                    Acoustics(pier, 0.9f, 1f, 1f);
                }

                for (int r = -1; r <= 1; r += 2)
                {
                    float railZ = z + r * (deckWidth * 0.5f - 0.2f);

                    Detail(bridge, $"Rail_{r}",
                        new Vector3(_riverCentreX, deckY + 0.85f, railZ),
                        new Vector3(span, 0.16f, 0.16f), _darkTimber);

                    for (int post = 0; post < 6; post++)
                    {
                        float t = post / 5f;
                        float x = Mathf.Lerp(_riverCentreX - span * 0.46f, _riverCentreX + span * 0.46f, t);
                        Detail(bridge, $"RailPost_{r}_{post}",
                            new Vector3(x, deckY + 0.5f, railZ),
                            new Vector3(0.16f, 0.9f, 0.16f), _darkTimber);
                    }

                    // Lanterns on the end posts, where a bridge keeper would actually hang them.
                    for (int end = -1; end <= 1; end += 2)
                    {
                        float x = _riverCentreX + end * span * 0.46f;
                        Detail(bridge, $"LampPost_{r}_{end}",
                            new Vector3(x, deckY + 1.1f, railZ),
                            new Vector3(0.2f, 2.1f, 0.2f), _darkTimber);
                        CreateLantern(bridge, new Vector3(x, deckY + 1.95f, railZ), 11f, 0.95f);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- pagoda

        /// <summary>
        /// A pagoda: storeys of shrinking footprint, each with a walkable balcony under a hip roof,
        /// stacked to a finial.
        ///
        /// Built to be climbed. Every balcony is a real surface, every eave carries a grapple
        /// anchor, and the storeys are spaced so a grapple from one balcony reaches the next -
        /// which makes a tower a route to the rooftops rather than scenery.
        /// </summary>
        private void BuildPagoda(Vector3 origin, int salt)
        {
            var pagoda = new GameObject($"Pagoda_{salt}").transform;
            pagoda.SetParent(_root, false);
            pagoda.localPosition = origin;
            _pagodas++;

            float footprint = BlockSize * 0.52f;
            const float storeyHeight = 4.6f;
            const float balconyWidth = 1.6f;

            Transform podium = Box(pagoda, "Podium", new Vector3(0f, 0.45f, 0f),
                new Vector3(footprint + 4f, 0.9f, footprint + 4f), UnseenLayers.Default, _stone);
            Acoustics(podium, 0.9f, 1.1f, 1.1f);

            float y = 0.9f;

            for (int storey = 0; storey < PagodaStoreys; storey++)
            {
                float shrink = 1f - storey * (0.62f / PagodaStoreys);
                float size = footprint * shrink;

                // One side open per storey, so the inside is enterable and the spiral of openings
                // gives a climber somewhere to go.
                for (int side = 0; side < 4; side++)
                {
                    if (side == storey % 4) continue;

                    bool horizontal = side % 2 == 0;
                    float sign = side < 2 ? 1f : -1f;

                    Transform wall = Box(pagoda, $"Wall_{storey}_{side}",
                        horizontal
                            ? new Vector3(0f, y + storeyHeight * 0.5f, size * 0.5f * sign)
                            : new Vector3(size * 0.5f * sign, y + storeyHeight * 0.5f, 0f),
                        horizontal
                            ? new Vector3(size, storeyHeight, 0.4f)
                            : new Vector3(0.4f, storeyHeight, size),
                        UnseenLayers.Occluder, _plaster);
                    Acoustics(wall, 0.75f, 1f, 1f);
                }

                Transform floor = Box(pagoda, $"Floor_{storey}", new Vector3(0f, y, 0f),
                    new Vector3(size, 0.3f, size), UnseenLayers.Default, _woodFloor);
                Acoustics(floor, 0.6f, 0.7f, 0.8f);

                for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Detail(pagoda, $"Post_{storey}_{sx}_{sz}",
                        new Vector3(sx * size * 0.5f, y + storeyHeight * 0.5f, sz * size * 0.5f),
                        new Vector3(0.5f, storeyHeight, 0.5f), _darkTimber);

                float balconyY = y + storeyHeight;
                float balconySpan = size + balconyWidth * 2f;

                Transform balcony = Box(pagoda, $"Balcony_{storey}", new Vector3(0f, balconyY, 0f),
                    new Vector3(balconySpan, 0.3f, balconySpan), UnseenLayers.Default, _woodFloor);
                Acoustics(balcony, 0.55f, 1.2f, 1.3f);

                for (int side = 0; side < 4; side++)
                {
                    bool horizontal = side % 2 == 0;
                    float sign = side < 2 ? 1f : -1f;
                    float edge = balconySpan * 0.5f - 0.15f;

                    Detail(pagoda, $"BalconyRail_{storey}_{side}",
                        horizontal
                            ? new Vector3(0f, balconyY + 0.75f, edge * sign)
                            : new Vector3(edge * sign, balconyY + 0.75f, 0f),
                        horizontal
                            ? new Vector3(balconySpan, 0.14f, 0.14f)
                            : new Vector3(0.14f, 0.14f, balconySpan),
                        _darkTimber);
                }

                BuildPagodaRoof(pagoda, balconySpan + 2.2f, balconyY + 0.3f, storey);

                // Hung under the eave at each balcony corner, on a real bracket.
                HangLantern(pagoda, new Vector3(balconySpan * 0.5f - 0.5f, balconyY + 1.9f,
                    balconySpan * 0.5f - 0.5f), 9f, 0.85f);

                y = balconyY + 1.9f;
            }

            Transform mast = Box(pagoda, "Finial", new Vector3(0f, y + 2.2f, 0f),
                new Vector3(0.9f, 4.4f, 0.9f), UnseenLayers.GrappleAnchor, _darkTimber);
            Acoustics(mast, 0.5f, 1f, 1f);
        }

        /// <summary>A pagoda roof: flatter and wider than a house roof, with corner brackets.</summary>
        private void BuildPagodaRoof(Transform pagoda, float span, float baseY, int storey)
        {
            const int tiers = 3;
            const float riser = 0.32f;
            float inset = span * 0.11f;

            float y = baseY + 0.2f;
            for (int i = 0; i < tiers; i++)
            {
                float size = span - inset * 2f * i;
                var host = new GameObject($"Roof_{storey}_{i}");
                host.transform.SetParent(pagoda, false);
                host.transform.localPosition = new Vector3(0f, y, 0f);
                host.layer = UnseenLayers.Default;

                var slab = new Vector3(size, 0.3f, size);
                host.AddComponent<MeshFilter>().sharedMesh = BoxMeshFactory.Get(slab, RoofTextureMetres);
                host.AddComponent<MeshRenderer>().sharedMaterial = _tile;
                host.AddComponent<BoxCollider>().size = slab;
                host.isStatic = true;
                Acoustics(host.transform, 0.85f, 1.35f, 1.5f);

                y += riser;
            }

            float corner = span * 0.5f - 0.6f;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                Transform hook = Box(pagoda, $"EaveHook_{storey}_{sx}_{sz}",
                    new Vector3(sx * corner, baseY - 0.35f, sz * corner),
                    new Vector3(0.9f, 0.6f, 0.9f), UnseenLayers.GrappleAnchor, _darkTimber);
                Acoustics(hook, 0.4f, 1f, 1f);
            }
        }

        // ---------------------------------------------------------------- foliage

        /// <summary>
        /// Trees and shrubs along the streets and the riverbank.
        ///
        /// These are cover, not decoration. A trunk is an occluder like any wall, so a tree breaks
        /// a sightline down a long street; the canopy above it is decoration only, because a
        /// canopy that blocked sight would hide rooftop ninjas from the ground for free.
        /// Shrubs sit on the Foliage layer, which the light raycasts already treat as shade -
        /// standing in one genuinely makes you harder to see.
        /// </summary>
        private void BuildFoliage(float extent, float pitch)
        {
            var grove = new GameObject("Foliage").transform;
            grove.SetParent(_root, false);

            int trees = 0;
            int shrubs = 0;

            for (int gx = 0; gx <= GridSize; gx++)
            for (int gz = 0; gz <= GridSize; gz++)
            {
                float streetX = (gx - GridSize * 0.5f) * pitch;
                float streetZ = (gz - GridSize * 0.5f) * pitch;

                // Corners are for lamp posts; trees go along the streets between them.
                for (int step = 1; step <= 2; step++)
                {
                    if (_random.NextDouble() > 0.34) continue;

                    bool alongZ = _random.NextDouble() < 0.5;
                    float slide = pitch * (step / 3f);

                    var spot = new Vector3(
                        alongZ ? streetX + (float)(_random.NextDouble() - 0.5) * 3f : streetX + slide,
                        0f,
                        alongZ ? streetZ + slide : streetZ + (float)(_random.NextDouble() - 0.5) * 3f);

                    if (Mathf.Abs(spot.x) > extent || Mathf.Abs(spot.z) > extent) continue;
                    if (_riverColumn >= 0 && Mathf.Abs(spot.x - _riverCentreX) < RiverWidth * 0.75f) continue;

                    if (_random.NextDouble() < 0.62)
                    {
                        BuildTree(grove, spot);
                        trees++;
                    }
                    else
                    {
                        BuildShrub(grove, spot);
                        shrubs++;
                    }
                }
            }

            _trees = trees;
            _shrubs = shrubs;
        }

        /// <summary>A pine: a trunk that blocks sight, and a stepped canopy that does not.</summary>
        private void BuildTree(Transform grove, Vector3 position)
        {
            var tree = new GameObject("Tree").transform;
            tree.SetParent(grove, false);
            tree.localPosition = position;

            float height = 4.5f + (float)_random.NextDouble() * 3.5f;
            float spread = 2.2f + (float)_random.NextDouble() * 1.6f;

            Transform trunk = Box(tree, "Trunk", new Vector3(0f, height * 0.5f, 0f),
                new Vector3(0.42f, height, 0.42f), UnseenLayers.Occluder, _darkTimber);
            Acoustics(trunk, 0.6f, 1f, 1f);

            // Canopy in three shrinking tiers, which reads as a pine at a distance and costs
            // three boxes. No colliders: you cannot stand on a tree, and it must not block sight.
            for (int tier = 0; tier < 3; tier++)
            {
                float t = tier / 2f;
                float y = Mathf.Lerp(height * 0.55f, height + 0.6f, t);
                float size = Mathf.Lerp(spread, spread * 0.35f, t);

                Detail(tree, $"Canopy_{tier}", new Vector3(0f, y, 0f),
                    new Vector3(size, 1.1f, size), _foliage);
            }
        }

        /// <summary>A clipped shrub. Low enough to crouch behind, and it counts as shade.</summary>
        private void BuildShrub(Transform grove, Vector3 position)
        {
            var shrub = new GameObject("Shrub").transform;
            shrub.SetParent(grove, false);
            shrub.localPosition = position;

            float width = 1.4f + (float)_random.NextDouble() * 1.1f;
            float height = 0.9f + (float)_random.NextDouble() * 0.6f;

            var host = new GameObject("Bush");
            host.transform.SetParent(shrub, false);
            host.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            host.layer = UnseenLayers.Foliage;

            var size = new Vector3(width, height, width);
            host.AddComponent<MeshFilter>().sharedMesh = BoxMeshFactory.Get(size, _textureMetres);
            host.AddComponent<MeshRenderer>().sharedMaterial = _foliage;
            host.AddComponent<BoxCollider>().size = size;
            host.isStatic = true;
            Acoustics(host.transform, 0.25f, 0.7f, 0.8f);
        }

        // ---------------------------------------------------------------- cost control

        /// <summary>
        /// Caps how many lantern lights are live at once.
        ///
        /// A 16x16 town has around a thousand lanterns, and a thousand real-time point lights is
        /// not something any renderer will do at frame rate. The gameplay light is untouched: the
        /// stealth index reads StealthLightSource, which never turns off. This only governs which
        /// lanterns are lit for the eye, nearest first.
        /// </summary>
        private void BudgetLanternLights()
        {
            _root.gameObject.AddComponent<LanternLightBudget>().Collect();
        }

        /// <summary>
        /// Merges each building into combined meshes.
        ///
        /// At this size the town is tens of thousands of renderers, and per-renderer culling and
        /// draw-call setup dominates long before the triangles do. Combining per building keeps the
        /// individual renderers - so a sliced shoji still hides its own paper - while giving the
        /// batcher one mesh per structure to work with.
        /// </summary>
        private void CombineStatics()
        {
            foreach (Transform child in _root)
            {
                if (child.childCount == 0) continue;
                StaticBatchingUtility.Combine(child.gameObject);
            }
        }

        /// <summary>Corner posts and an eave fascia on one storey of the keep. Decorative only.</summary>
        private void DressKeepStorey(Transform keep, float y, float storeyHeight, float size)
        {
            float corner = size * 0.5f;

            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
                Detail(keep, $"KeepPost_{y:0}_{sx}_{sz}",
                    new Vector3(sx * corner, y + storeyHeight * 0.5f, sz * corner),
                    new Vector3(0.7f, storeyHeight, 0.7f), _darkTimber);

            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                float outward = (size + 3f) * 0.5f;

                Detail(keep, $"KeepFascia_{y:0}_{side}",
                    horizontal
                        ? new Vector3(0f, y + storeyHeight - 0.16f, outward * sign)
                        : new Vector3(outward * sign, y + storeyHeight - 0.16f, 0f),
                    horizontal
                        ? new Vector3(size + 3f, 0.34f, 0.2f)
                        : new Vector3(0.2f, 0.34f, size + 3f),
                    _darkTimber);
            }
        }

        /// <summary>
        /// Post-and-beam framing over one plaster wall: a stone plinth at the foot, uprights at
        /// regular bays, a nuki rail through the middle and a head beam under the eave.
        ///
        /// Every piece is decorative. The wall behind it is the only collider, occluder and
        /// acoustic surface on this side of the building.
        /// </summary>
        private void DressWall(Transform compound, int side, bool horizontal, float sign,
            float half, float height, bool hasDoorway)
        {
            const float faceOffset = 0.22f; // just proud of the 0.4 m wall, so nothing z-fights
            const int bays = 5;

            float outward = (half + faceOffset) * sign;
            float length = BlockSize;

            // Axis helper: pieces on a north/south wall run along x, pieces on an east/west wall
            // run along z, and the two swap which component of the size vector is the thickness.
            Vector3 Along(float distance, float y) => horizontal
                ? new Vector3(distance, y, outward)
                : new Vector3(outward, y, distance);

            Vector3 Size(float run, float y, float thickness) => horizontal
                ? new Vector3(run, y, thickness)
                : new Vector3(thickness, y, run);

            Detail(compound, $"Plinth_{side}", Along(0f, 0.35f), Size(length, 0.7f, 0.24f), _stone);

            Detail(compound, $"HeadBeam_{side}", Along(0f, height - 0.28f), Size(length, 0.4f, 0.2f),
                _darkTimber);

            Detail(compound, $"Nuki_{side}", Along(0f, height * 0.56f), Size(length, 0.2f, 0.16f),
                _darkTimber);

            // Uprights. The doorway sits in the middle of its wall, so that bay is skipped rather
            // than posting a beam across the opening.
            for (int i = 0; i <= bays; i++)
            {
                float t = i / (float)bays;
                float distance = Mathf.Lerp(-length * 0.5f, length * 0.5f, t);
                if (hasDoorway && Mathf.Abs(distance) < 2.2f) continue;

                Detail(compound, $"Post_{side}_{i}", Along(distance, (height + 0.7f) * 0.5f),
                    Size(0.32f, height - 0.7f, 0.18f), _darkTimber);
            }

            // Fascia board along the eave, which is what gives the roofline its dark edge.
            Detail(compound, $"Fascia_{side}",
                horizontal
                    ? new Vector3(0f, height + 0.02f, (half + EaveOverhang) * sign)
                    : new Vector3((half + EaveOverhang) * sign, height + 0.02f, 0f),
                Size(BlockSize + EaveOverhang * 2f, 0.34f, 0.18f), _darkTimber);
        }

        /// <summary>Timber posts and a lintel around the 3 m gap in a compound wall.</summary>
        private void DressDoorway(Transform compound, bool horizontal, float sign, float half, float height)
        {
            float outward = half * sign;
            float lintelY = 2.6f;

            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 position = horizontal
                    ? new Vector3(s * 1.65f, lintelY * 0.5f, outward)
                    : new Vector3(outward, lintelY * 0.5f, s * 1.65f);
                Vector3 size = horizontal
                    ? new Vector3(0.34f, lintelY, 0.56f)
                    : new Vector3(0.56f, lintelY, 0.34f);

                Detail(compound, $"DoorPost_{s}", position, size, _darkTimber);
            }

            Detail(compound, "DoorLintel",
                horizontal
                    ? new Vector3(0f, lintelY + 0.2f, outward)
                    : new Vector3(outward, lintelY + 0.2f, 0f),
                horizontal ? new Vector3(3.9f, 0.4f, 0.56f) : new Vector3(0.56f, 0.4f, 3.9f),
                _darkTimber);

            // Noren: the split curtain hung in a shop doorway. Paper rather than cloth in the
            // material set, but at night, lit from inside, it reads correctly.
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 position = horizontal
                    ? new Vector3(s * 0.8f, lintelY - 0.5f, outward)
                    : new Vector3(outward, lintelY - 0.5f, s * 0.8f);
                Vector3 size = horizontal
                    ? new Vector3(1.4f, 1f, 0.06f)
                    : new Vector3(0.06f, 1f, 1.4f);

                Detail(compound, $"Noren_{s}", position, size, _paper);
            }

            // A lantern on each door post, facing out into the street. This is where a lamp goes
            // on a real building: at head height by the entrance, lighting the threshold.
            Vector3 face = horizontal ? new Vector3(0f, 0f, sign) : new Vector3(sign, 0f, 0f);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 anchor = horizontal
                    ? new Vector3(s * 1.65f, lintelY - 0.15f, outward)
                    : new Vector3(outward, lintelY - 0.15f, s * 1.65f);

                MountLantern(compound, anchor, face, 10f, 1f);
            }
        }

        /// <summary>
        /// A hipped tile roof approximated as stacked, shrinking slabs, and returns the height of
        /// the top surface.
        ///
        /// Steps rather than a true slope on purpose: roofs are a traversal route, and each riser
        /// is kept under the character controller's 0.45 m step offset so a ninja walks up the
        /// roof instead of being stopped by it. A real sloped mesh would also need its own
        /// collider, where these reuse the cached box meshes the rest of the town is built from.
        /// </summary>
        private float BuildHipRoof(Transform compound, float eaveSpan, float height)
        {
            // Six shallow risers rather than three deep ones: the pitch is what makes it read as a
            // roof, and a wider step at the same height just reads as a terrace.
            const int tiers = 6;
            const float riser = 0.38f;
            const float slabThickness = 0.4f;
            float inset = 1.15f;

            float y = height + 0.18f;
            for (int i = 0; i < tiers; i++)
            {
                float span = eaveSpan - inset * 2f * i;
                var tierHost = new GameObject($"Roof_{i}");
                tierHost.transform.SetParent(compound, false);
                tierHost.transform.localPosition = new Vector3(0f, y, 0f);
                tierHost.layer = UnseenLayers.Default;

                var size = new Vector3(span, slabThickness, span);

                // A finer texture scale than the rest of the town: at the shared 2.5 m repeat a
                // roof tile came out the size of a paving slab, and the steps read as masonry
                // stairs instead of courses of tiles.
                tierHost.AddComponent<MeshFilter>().sharedMesh = BoxMeshFactory.Get(size, RoofTextureMetres);
                tierHost.AddComponent<MeshRenderer>().sharedMaterial = _tile;
                tierHost.AddComponent<BoxCollider>().size = size;
                tierHost.isStatic = true;

                Transform tier = tierHost.transform;
                Acoustics(tier, 0.85f, 1.35f, 1.5f);

                // Course edge: a dark lip on the riser, which is what gives a tiled roof its
                // banding when seen from the side.
                if (i < tiers - 1)
                {
                    float nextSpan = eaveSpan - inset * 2f * (i + 1);
                    for (int side = 0; side < 4; side++)
                    {
                        bool horizontal = side % 2 == 0;
                        float sign = side < 2 ? 1f : -1f;
                        float lip = nextSpan * 0.5f + 0.12f;

                        Detail(compound, $"Course_{i}_{side}",
                            horizontal
                                ? new Vector3(0f, y + riser * 0.5f, lip * sign)
                                : new Vector3(lip * sign, y + riser * 0.5f, 0f),
                            horizontal
                                ? new Vector3(nextSpan + 0.24f, riser * 0.55f, 0.16f)
                                : new Vector3(0.16f, riser * 0.55f, nextSpan + 0.24f),
                            _darkTimber);
                    }
                }

                y += riser;
            }

            float top = y - riser + slabThickness * 0.5f;

            // Grapple anchors on the eave corners, not just the ridge. A ridge anchor sits behind
            // its own roof from every street, so the rope-path check refused every shot at it and
            // the hook read as broken. A corner bracket on the eave is the thing you can actually
            // see and throw a line to from below.
            float eaveHalf = eaveSpan * 0.5f - 0.5f;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                // Hung UNDER the eave, not level with it. Sat at roof height the bracket was
                // inside the roof slab, and the rope-path check then found the roof itself
                // blocking every shot from the street - which is exactly how a working grapple
                // reads as a dead button.
                Transform hook = Box(compound, $"EaveHook_{sx}_{sz}",
                    new Vector3(sx * eaveHalf, height - 0.45f, sz * eaveHalf),
                    new Vector3(0.8f, 0.6f, 0.8f), UnseenLayers.GrappleAnchor, _darkTimber);
                Acoustics(hook, 0.4f, 1f, 1f);
            }

            // Ridge caps along the crown, and the heavy end blocks that sit on the hips.
            float crown = eaveSpan - inset * 2f * (tiers - 1);
            Detail(compound, "RidgeCap", new Vector3(0f, top + 0.16f, 0f),
                new Vector3(crown * 0.55f, 0.32f, 0.8f), _darkTimber);

            for (int sx = -1; sx <= 1; sx += 2)
                Detail(compound, $"Onigawara_{sx}",
                    new Vector3(sx * crown * 0.28f, top + 0.34f, 0f),
                    new Vector3(0.9f, 0.66f, 1.1f), _darkTimber);

            return top;
        }

        /// <summary>A run of shoji panels along one axis, each an independent destructible.</summary>
        private void BuildShojiRun(Transform parent, Vector3 centre, float length, bool alongX, float height)
        {
            const float panelWidth = 2.6f;
            int panels = Mathf.Max(1, Mathf.RoundToInt(length / panelWidth));
            float actual = length / panels;

            for (int i = 0; i < panels; i++)
            {
                float offset = -length * 0.5f + actual * (i + 0.5f);
                Vector3 position = centre + (alongX ? new Vector3(offset, 0f, 0f) : new Vector3(0f, 0f, offset));

                var panelHost = new GameObject($"Shoji_{(alongX ? "X" : "Z")}_{i}").transform;
                panelHost.SetParent(parent, false);
                panelHost.localPosition = position;
                panelHost.localRotation = Quaternion.Euler(0f, alongX ? 0f : 90f, 0f);

                Vector3 size = new Vector3(actual - 0.15f, height * 0.86f, 0.08f);

                Transform paper = Box(panelHost, "Paper", Vector3.zero, size, UnseenLayers.ShojiPaper,
                    _shojiPaper);
                Acoustics(paper, 0.12f, 1f, 1f);

                Transform frame = Box(panelHost, "Frame",
                    new Vector3(0f, -height * 0.43f, 0f),
                    new Vector3(actual, 0.18f, 0.16f), UnseenLayers.Default, _timber);
                Acoustics(frame, 0.4f, 1f, 1f);

                ShojiPanel panel = panelHost.gameObject.AddComponent<ShojiPanel>();
                panel.PaperCollider = paper.GetComponent<Collider>();
                panel.FrameCollider = frame.GetComponent<Collider>();
                panel.PaperRenderer = paper.GetComponent<Renderer>();
                panel.EnsureRegistered();
            }
        }

        private void BuildKeep(Vector3 origin)
        {
            var keep = new GameObject("Keep").transform;
            keep.SetParent(_root, false);
            keep.localPosition = origin;

            int storeys = 3;
            float storeyHeight = 5.2f;
            float footprint = BlockSize * 0.8f;

            for (int s = 0; s < storeys; s++)
            {
                float y = s * storeyHeight;
                float shrink = 1f - s * 0.14f;
                float size = footprint * shrink;

                // Four walls per storey with a gap on alternating sides, so the keep is climbable
                // and infiltratable rather than a sealed box.
                for (int side = 0; side < 4; side++)
                {
                    if (side == (s + 1) % 4) continue;

                    bool horizontal = side % 2 == 0;
                    float sign = side < 2 ? 1f : -1f;
                    Vector3 centre = horizontal
                        ? new Vector3(0f, y + storeyHeight * 0.5f, size * 0.5f * sign)
                        : new Vector3(size * 0.5f * sign, y + storeyHeight * 0.5f, 0f);
                    Vector3 wallSize = horizontal
                        ? new Vector3(size, storeyHeight, 0.5f)
                        : new Vector3(0.5f, storeyHeight, size);

                    Transform wall = Box(keep, $"KeepWall_{s}_{side}", centre, wallSize, UnseenLayers.Occluder, _stone);
                    Acoustics(wall, 0.95f, 1f, 1f);
                }

                Transform floor = Box(keep, $"KeepFloor_{s}",
                    new Vector3(0f, y, 0f), new Vector3(size, 0.4f, size), UnseenLayers.Default, _stone);
                Acoustics(floor, 0.9f, 1.1f, 1.1f);

                Transform eave = Box(keep, $"KeepEave_{s}",
                    new Vector3(0f, y + storeyHeight, 0f),
                    new Vector3(size + 3f, 0.4f, size + 3f), UnseenLayers.Default, _tile);
                Acoustics(eave, 0.85f, 1.3f, 1.4f);

                DressKeepStorey(keep, y, storeyHeight, size);
                PlaceLanterns(keep, size * 0.5f, y + storeyHeight * 0.6f);
            }

            float keepRoof = BuildHipRoof(keep, footprint * (1f - storeys * 0.14f) + 3f,
                storeys * storeyHeight);

            Transform anchor = Box(keep, "KeepAnchor",
                new Vector3(0f, keepRoof + 0.7f, 0f),
                new Vector3(4f, 1.4f, 4f), UnseenLayers.GrappleAnchor, _tile);
            Acoustics(anchor, 0.6f, 1.2f, 1.2f);

            // The keep is the richest loot site and therefore the busiest early fight.
            for (int i = 0; i < 4; i++) PlaceContainers(keep, footprint * 0.35f);
        }

        private void BuildSewerNetwork(float extent, float pitch)
        {
            var sewers = new GameObject("Sewers").transform;
            sewers.SetParent(_root, false);

            float y = -SewerDepth;
            int lines = GridSize;

            for (int i = 0; i < lines; i++)
            {
                float offset = (i - (lines - 1) * 0.5f) * pitch;

                BuildSewerCorridor(sewers, new Vector3(offset, y, 0f), new Vector3(SewerCorridorWidth, SewerHeight, extent * 2f), $"NS_{i}");
                BuildSewerCorridor(sewers, new Vector3(0f, y, offset), new Vector3(extent * 2f, SewerHeight, SewerCorridorWidth), $"EW_{i}");

                // Access shaft up into the street, the only way in or out.
                Transform shaft = Box(sewers, $"Shaft_{i}",
                    new Vector3(offset, y * 0.5f, offset),
                    new Vector3(2.4f, SewerDepth, 2.4f), UnseenLayers.Climbable, _stone);
                Acoustics(shaft, 0.8f, 1f, 1.4f);
            }
        }

        private void BuildSewerCorridor(Transform parent, Vector3 centre, Vector3 size, string name)
        {
            Transform floor = Box(parent, $"SewerFloor_{name}",
                centre + new Vector3(0f, -size.y * 0.5f, 0f),
                new Vector3(size.x, 0.4f, size.z), UnseenLayers.Default, _stone);
            Acoustics(floor, 0.95f, 1.4f, 1.6f);

            Transform ceiling = Box(parent, $"SewerCeiling_{name}",
                centre + new Vector3(0f, size.y * 0.5f, 0f),
                new Vector3(size.x, 0.4f, size.z), UnseenLayers.Occluder, _stone);
            Acoustics(ceiling, 0.98f, 1f, 1f);

            bool alongZ = size.z > size.x;
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 offset = alongZ
                    ? new Vector3(size.x * 0.5f * s, 0f, 0f)
                    : new Vector3(0f, 0f, size.z * 0.5f * s);
                Vector3 wallSize = alongZ
                    ? new Vector3(0.4f, size.y, size.z)
                    : new Vector3(size.x, size.y, 0.4f);

                Transform wall = Box(parent, $"SewerWall_{name}_{s}", centre + offset, wallSize,
                    UnseenLayers.Occluder, _stone);
                Acoustics(wall, 0.98f, 1f, 1f);
            }
        }

        private void BuildStreetLanterns(float extent, float pitch)
        {
            var street = new GameObject("StreetLights").transform;
            street.SetParent(_root, false);

            for (int gx = 0; gx <= GridSize; gx++)
            for (int gz = 0; gz <= GridSize; gz++)
            {
                if (_random.NextDouble() > 0.55) continue;

                var ground = new Vector3(
                    (gx - GridSize * 0.5f) * pitch,
                    0f,
                    (gz - GridSize * 0.5f) * pitch);

                // Do not plant a lamp post in the river.
                if (_riverColumn >= 0 && Mathf.Abs(ground.x - _riverCentreX) < RiverWidth) continue;

                PostLantern(street, ground, 3.4f, 11f, 1.05f);
            }
        }

        // ---------------------------------------------------------------- props

        /// <summary>
        /// Interior lanterns, hung from the rafters rather than floating at a random height.
        ///
        /// The rafter run is at a known height and three known z positions, so a lantern can be
        /// put under an actual beam with a cord that actually reaches it. A lamp hanging off
        /// nothing in the middle of a room is the single loudest tell that a scene is generated.
        /// </summary>
        private void PlaceLanterns(Transform parent, float half, float height)
        {
            int count = Mathf.RoundToInt(LanternsPerCompound * (0.6f + (float)_random.NextDouble() * 0.8f));
            float ceiling = height - 0.75f;

            for (int i = 0; i < count; i++)
            {
                // Line up with one of the three rafters, then slide along it.
                int rafter = _random.Next(3);
                float z = Mathf.Lerp(-half + 2f, half - 2f, (rafter + 1f) / 4f);
                float x = (float)(_random.NextDouble() * 2f - 1f) * (half - 3.5f);

                float drop = 0.7f + (float)_random.NextDouble() * 0.8f;
                HangLantern(parent, new Vector3(x, ceiling - drop, z), 8f, 0.95f);
            }
        }

        /// <summary>
        /// A lantern on a cord, with the cord drawn all the way up to whatever is above it.
        /// The caller is responsible for there being something up there to hang from.
        /// </summary>
        private void HangLantern(Transform parent, Vector3 position, float radius, float intensity)
        {
            CreateLantern(parent, position, radius, intensity);

            // Traced upward so the cord ends at the beam, not at a guessed length.
            float drop = 1.2f;
            Vector3 world = parent.TransformPoint(position + Vector3.up * 0.32f);
            if (Physics.Raycast(world, Vector3.up, out RaycastHit hit, 6f,
                    UnseenLayers.WorldGeometry | (1 << UnseenLayers.Rafter),
                    QueryTriggerInteraction.Ignore))
                drop = Mathf.Max(0.2f, hit.distance);

            Detail(parent, "Cord", position + Vector3.up * (0.32f + drop * 0.5f),
                new Vector3(0.04f, drop, 0.04f), _rafter);
        }

        /// <summary>
        /// A lantern on a wall bracket: an arm out from the wall and a short drop to the lantern.
        /// This is how the ones flanking a doorway are mounted.
        /// </summary>
        private void MountLantern(Transform parent, Vector3 wallPoint, Vector3 outward,
            float radius, float intensity)
        {
            const float reach = 0.62f;
            Vector3 arm = wallPoint + outward * (reach * 0.5f);
            Vector3 hang = wallPoint + outward * reach;

            Detail(parent, "Bracket", arm,
                new Vector3(
                    Mathf.Abs(outward.x) > 0.5f ? reach : 0.12f,
                    0.12f,
                    Mathf.Abs(outward.z) > 0.5f ? reach : 0.12f),
                _darkTimber);

            Detail(parent, "BracketDrop", hang + Vector3.down * 0.22f,
                new Vector3(0.08f, 0.44f, 0.08f), _darkTimber);

            CreateLantern(parent, hang + Vector3.down * 0.72f, radius, intensity);
        }

        /// <summary>A free-standing street lamp: a post from the ground with a lantern on top.</summary>
        private void PostLantern(Transform parent, Vector3 groundPoint, float height,
            float radius, float intensity)
        {
            // Solid, unlike the rest of the trim: a post planted in the middle of a street is
            // something you walk into.
            Transform post = Box(parent, "LampPost", groundPoint + Vector3.up * (height * 0.5f),
                new Vector3(0.22f, height, 0.22f), UnseenLayers.Occluder, _darkTimber);
            Acoustics(post, 0.4f, 1f, 1f);

            Detail(parent, "LampHead", groundPoint + Vector3.up * (height + 0.08f),
                new Vector3(0.55f, 0.16f, 0.55f), _darkTimber);

            CreateLantern(parent, groundPoint + Vector3.up * (height - 0.45f), radius, intensity);
        }

        private void CreateLantern(Transform parent, Vector3 localPosition, float radius, float intensity)
        {
            var host = new GameObject("Lantern").transform;
            host.SetParent(parent, false);
            host.localPosition = localPosition;

            // Paper body: a lathe-turned chochin rather than a cube, with the ribbed paper texture
            // wrapped around it.
            var shellHost = new GameObject("Shell");
            shellHost.transform.SetParent(host, false);
            shellHost.layer = UnseenLayers.Interactable;

            var shellFilter = shellHost.AddComponent<MeshFilter>();
            shellFilter.sharedMesh = LanternMeshFactory.Get(0.44f, 0.62f);

            var shellRenderer = shellHost.AddComponent<MeshRenderer>();
            shellRenderer.sharedMaterial = _lanternGlow != null ? _lanternGlow : _paper;

            // The collider stays a simple box: it is what a shuriken has to hit, and a lathe mesh
            // collider would cost far more than the accuracy is worth.
            var shellCollider = shellHost.AddComponent<BoxCollider>();
            shellCollider.size = new Vector3(0.44f, 0.62f, 0.44f);

            Transform shell = shellHost.transform;
            Acoustics(shell, 0.05f, 1f, 1f);

            // No timber cap or base ring on purpose. The lamp sits a few centimetres inside the
            // paper and casts no shadow (94 lanterns, shadows off), so any flat timber face near
            // it renders as a blown-white plate. The rolled-in ends already close the shape.

            Light light = host.gameObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = radius;
            light.color = new Color(1f, 0.78f, 0.45f);

            // The gameplay intensity (StealthLightSource) and the rendered intensity are separate
            // numbers on purpose, but they have to agree perceptually or the player cannot read the
            // light they are being seen by.
            light.intensity = intensity * LanternVisualIntensity;
            light.shadows = LightShadows.None;

            StealthLightSource source = host.gameObject.AddComponent<StealthLightSource>();
            source.Radius = radius;
            source.Intensity = intensity;
            source.Visual = light;
            source.EnsureRegistered();

            Lantern lantern = host.gameObject.AddComponent<Lantern>();
            lantern.Source = source;
            lantern.EnsureRegistered();
        }

        private void PlaceContainers(Transform parent, float half)
        {
            int count = Mathf.RoundToInt(ContainersPerCompound * (0.5f + (float)_random.NextDouble()));
            for (int i = 0; i < count; i++)
            {
                var position = new Vector3(
                    (float)(_random.NextDouble() * 2f - 1f) * (half - 2f),
                    0.45f,
                    (float)(_random.NextDouble() * 2f - 1f) * (half - 2f));

                var host = new GameObject("Chest").transform;
                host.SetParent(parent, false);
                host.localPosition = position;

                Transform box = Box(host, "Body", Vector3.zero, new Vector3(1f, 0.7f, 0.7f),
                    UnseenLayers.LootContainer, _woodFloor);
                Acoustics(box, 0.5f, 1f, 1f);

                LootContainer container = host.gameObject.AddComponent<LootContainer>();
                container.Table = Table != null ? Table : EnsureRuntimeLootTable();
                container.EnsureRegistered();
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// A decorative box: mesh and renderer only, no collider and no acoustic material.
        ///
        /// Trim has to stay physically inert. A post modelled on the outside of a wall that also
        /// carried a collider would shift where a ninja stands, change what the parkour probes
        /// find, and add a second surface for the sound and light raycasts to hit - all so the
        /// building could look better. The wall behind it already does every one of those jobs.
        /// </summary>
        private Transform Detail(Transform parent, string name, Vector3 localPosition, Vector3 size,
            Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.layer = UnseenLayers.Default;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BoxMeshFactory.Get(size, _textureMetres);

            var renderer = go.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;

            go.isStatic = true;
            return go.transform;
        }

        private Transform Box(Transform parent, string name, Vector3 localPosition, Vector3 size, int layer, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.layer = layer;

            // The mesh carries the world size and its UV scale, so the transform stays at unit scale
            // and the collider carries the dimensions. That keeps one shared material per surface
            // type - and therefore SRP batching - while every face gets correct texture scale.
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BoxMeshFactory.Get(size, _textureMetres);

            var renderer = go.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;

            var box = go.AddComponent<BoxCollider>();
            box.size = size;

            go.isStatic = true;
            return go.transform;
        }

        private static void Acoustics(Transform target, float attenuation, float footstep, float radius)
        {
            AcousticMaterial material = target.gameObject.AddComponent<AcousticMaterial>();
            material.Attenuation = attenuation;
            material.FootstepScale = footstep;
            material.FootstepRadiusScale = radius;
        }

        private void CreateMaterials()
        {
            GreyboxMaterialSet set = MaterialSet != null ? MaterialSet : GreyboxMaterialSet.Load();
            if (set != null && set.IsComplete)
            {
                _set = set;
                _stone = set.Stone;
                _timber = set.Timber;
                _paper = set.Paper;
                _tile = set.RoofTile;
                _woodFloor = set.WoodFloor;
                _rafter = set.WoodFloor;
                _tatami = set.Tatami;
                _ground = set.Ground;
                _lanternGlow = set.LanternGlow;
                _plaster = set.Plaster != null ? set.Plaster : set.Stone;
                _darkTimber = set.DarkTimber != null ? set.DarkTimber : set.Timber;
                _water = set.Water != null ? set.Water : set.Stone;
                _foliage = set.Foliage != null ? set.Foliage : set.Timber;
                _shojiPaper = set.ShojiPaper != null ? set.ShojiPaper : set.Paper;
                _textureMetres = set.TextureMetres;
                _textured = true;
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");

            _stone = MakeMaterial(shader, "GreyboxStone", new Color(0.34f, 0.34f, 0.36f));
            _timber = MakeMaterial(shader, "GreyboxTimber", new Color(0.36f, 0.26f, 0.18f));
            _paper = MakeMaterial(shader, "GreyboxPaper", new Color(0.86f, 0.83f, 0.72f));
            _tile = MakeMaterial(shader, "GreyboxTile", new Color(0.22f, 0.24f, 0.28f));
            _rafter = MakeMaterial(shader, "GreyboxRafter", new Color(0.28f, 0.2f, 0.14f));

            // No art imported: reuse the flat colours so every call site still has a material.
            _woodFloor = _rafter;
            _tatami = _timber;
            _ground = _stone;
            _plaster = MakeMaterial(shader, "GreyboxPlaster", new Color(0.72f, 0.70f, 0.64f));
            _darkTimber = MakeMaterial(shader, "GreyboxDarkTimber", new Color(0.2f, 0.15f, 0.11f));
            _water = MakeMaterial(shader, "GreyboxWater", new Color(0.1f, 0.17f, 0.2f));
            _foliage = MakeMaterial(shader, "GreyboxFoliage", new Color(0.13f, 0.2f, 0.13f));
            _shojiPaper = _paper;
            _textured = false;
        }

        private static Material MakeMaterial(Shader shader, string name, Color colour)
        {
            var material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
            return material;
        }

        /// <summary>
        /// Builds a usable loot table in code so the greybox has real items with real modifiers
        /// before anyone authors a single asset.
        /// </summary>
        private LootTable EnsureRuntimeLootTable()
        {
            if (_runtimeTable != null) return _runtimeTable;

            _runtimeTable = ScriptableObject.CreateInstance<LootTable>();
            _runtimeTable.name = "RuntimeLootTable";
            _runtimeTable.RollsPerContainer = 2;
            _runtimeTable.Entries = new List<LootTable.Entry>
            {
                Entry(Weapon("katana", "Katana", WeaponClass.Katana, 1f, 0f, 1.4f), 30f),
                Entry(Weapon("kusarigama", "Kusarigama", WeaponClass.Kusarigama, 0.9f, 1.1f, 1.9f), 14f),
                Entry(Weapon("shuriken", "Shuriken", WeaponClass.Shuriken, 0.7f, 0f, 0.2f), 18f),
                Entry(Gear("tabi", "Soft-soled Tabi", 0.04f, 0.5f, 0.5f), 16f),
                Entry(Gear("darkcloth", "Dark Cloth", 0.08f, 1f, 1f), 12f),
                Entry(Utility("smoke", "Smoke Bomb", UtilityEffect.SmokeBomb, 6.5f, 8f, 0.8f, 18f), 20f),
                Entry(Utility("noisemaker", "Noisemaker", UtilityEffect.Noisemaker, 2f, 1f, 3.2f, 45f), 14f),
                Entry(Utility("elixir", "Night-vision Elixir", UtilityEffect.NightVisionElixir, 0f, 22f, 0.2f, 4f), 10f)
            };

            return _runtimeTable;
        }

        private static LootTable.Entry Entry(ItemDefinition item, float weight)
        {
            return new LootTable.Entry { Item = item, Weight = weight, MinZoneStage = 0 };
        }

        private static ItemDefinition Weapon(string id, string name, WeaponClass weaponClass,
            float damageScale, float reachBonus, float swingLoudness)
        {
            ItemDefinition item = ScriptableObject.CreateInstance<ItemDefinition>();
            item.name = id;
            item.Id = id;
            item.DisplayName = name;
            item.Kind = ItemKind.Weapon;
            item.Weapon = weaponClass;
            item.DamageScale = damageScale;
            item.ReachBonus = reachBonus;
            item.SwingLoudness = swingLoudness;
            item.SwingRadius = swingLoudness * 13f;
            item.WindupBonus = weaponClass == WeaponClass.Kusarigama ? 0.08f : 0f;
            return item;
        }

        private static ItemDefinition Gear(string id, string name, float stealthBonus,
            float footstepLoudness, float footstepRadius)
        {
            ItemDefinition item = ScriptableObject.CreateInstance<ItemDefinition>();
            item.name = id;
            item.Id = id;
            item.DisplayName = name;
            item.Kind = ItemKind.Gear;
            item.StealthBonus = stealthBonus;
            item.FootstepLoudnessScale = footstepLoudness;
            item.FootstepRadiusScale = footstepRadius;
            return item;
        }

        private static ItemDefinition Utility(string id, string name, UtilityEffect effect,
            float radius, float duration, float loudness, float soundRadius)
        {
            ItemDefinition item = ScriptableObject.CreateInstance<ItemDefinition>();
            item.name = id;
            item.Id = id;
            item.DisplayName = name;
            item.Kind = ItemKind.Utility;
            item.Effect = effect;
            item.EffectRadius = radius;
            item.EffectDuration = duration;
            item.EffectLoudness = loudness;
            item.EffectSoundRadius = soundRadius;
            item.StackSize = 2;
            return item;
        }
    }
}
