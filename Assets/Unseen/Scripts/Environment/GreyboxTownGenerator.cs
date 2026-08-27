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

        [Tooltip("Depth of the channel below street level. Deep enough to stand under a bridge " +
                 "with room over your head, and deep enough that the banks hide you from the " +
                 "streets either side.")]
        public float RiverDepth = 5.6f;

        [Tooltip("How high the middle of a bridge stands above its ends. A drum bridge is meant " +
                 "to be climbed, not strolled across.")]
        public float BridgeArchRise = 2.9f;

        [Tooltip("Width of the water itself. The towpaths sit either side of it.")]
        public float RiverWidth = 16f;

        [Tooltip("How far the spirit forest must stand above the tallest roof in the town, so " +
                 "that nothing can be dropped onto it from a pagoda.")]
        public float BambooClearance = 9f;

        [Tooltip("Depth of water above the bed. Most of the channel, so the river looks like one.")]
        public float WaterDepth = 2.6f;

        [Tooltip("Water over the shelves along each bank, in metres. Waist deep on a 1.8 m ninja.")]
        public float WadeShallow = 0.8f;

        [Tooltip("Water down the middle of the channel. Deep enough that crouching submerges you.")]
        public float WadeDeep = 1.35f;

        [Tooltip("How far the towpath ledge stands above the waterline.")]
        public float TowpathFreeboard = 0.55f;

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

        [Header("Street layout")]
        [Tooltip("How far a block may wander from its cell centre, in metres. Breaks the ruled " +
                 "line of a grid without letting neighbours touch.")]
        [Range(0f, 6f)] public float BlockJitter = 2.2f;

        [Tooltip("How far a block may turn off square, in degrees. The single strongest cue that " +
                 "a town grew rather than being set out.")]
        [Range(0f, 12f)] public float BlockRotation = 5f;

        [Tooltip("Smallest a block may shrink to, as a fraction of BlockSize. Uneven plot sizes " +
                 "widen some streets and pinch others.")]
        [Range(0.6f, 1f)] public float MinBlockScale = 0.82f;

        [Tooltip("How many discrete plot sizes exist between MinBlockScale and full size. Kept " +
                 "small on purpose: the box mesh cache is keyed on dimensions, so every extra " +
                 "size multiplies the number of meshes the town holds.")]
        [Range(1, 6)] public int BlockSizeSteps = 3;

        [Tooltip("How far alternate rows slide along the street, as a fraction of the block pitch. " +
                 "This is the dog-leg a castle town used deliberately: a cross-street that does " +
                 "not run straight cannot be charged down.")]
        [Range(0f, 0.5f)] public float RowStagger = 0.28f;

        [Tooltip("Fraction of cells left as open ground - a market square, a shrine yard, a gap.")]
        [Range(0f, 0.3f)] public float PlazaChance = 0.07f;

        [Tooltip("Fraction of blocks built as a kura: a fireproof storehouse with thick plaster " +
                 "walls, few openings and a heavy roof. Tall, blank and unclimbable.")]
        [Range(0f, 0.4f)] public float KuraChance = 0.12f;

        [Tooltip("Fraction built as a nagaya: a long low terrace of one-room dwellings, doors " +
                 "every few metres onto the street.")]
        [Range(0f, 0.4f)] public float NagayaChance = 0.12f;

        [Tooltip("Fraction built as a walled garden with a teahouse in it. Mostly open ground, " +
                 "which makes a compound-dense town breathe.")]
        [Range(0f, 0.3f)] public float TeahouseChance = 0.08f;

        [Tooltip("Fraction of open plazas given a small shrine and a torii gate.")]
        [Range(0f, 1f)] public float ShrineChance = 0.4f;

        [Tooltip("Chance an open cell is a raked gravel garden rather than a shrine yard.")]
        [Range(0f, 1f)] public float ZenGardenChance = 0.22f;

        [Tooltip("Chance an open cell is a rock garden with a waterfall.")]
        [Range(0f, 1f)] public float RockGardenChance = 0.2f;

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
        private int _kura;
        private int _nagaya;
        private int _gardens;
        private int _shrines;
        private int _zenGardens;
        private int _rockGardens;
        private int _waterfalls;
        private int _koi;
        private MapSketch _sketch;
        private bool _textured;
        private GreyboxMaterialSet _set;
        private Material _lanternGlow;
        private Material _plaster;
        private Material _darkTimber;
        private Material _water;
        private Material _foliage;
        private Material _shojiPaper;
        private Material _vermilion;
        private Material _bamboo;
        private Material _grass;
        private Material _dirt;
        private Material _reed;
        private Material _riverStone;
        private Material _groundMist;
        private Material _moss;
        private Material _bambooMass;
        private BambooForest _forest;
        private int _birds;
        private int _unhungLanterns;
        private int _stoodLanterns;
        private int _animals;

        /// <summary>The spirit forest, for the growth system to drive.</summary>
        public BambooForest Forest => _forest;
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

            _sketch = gameObject.GetComponent<MapSketch>();
            if (_sketch == null) _sketch = gameObject.AddComponent<MapSketch>();
            _sketch.Clear();

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

                // Alternate rows slide along the street, so no cross-street runs the full width of
                // the town. This is the kagimagari dog-leg a jokamachi was laid out with on
                // purpose - a straight road is a road cavalry can charge down - and it happens to
                // be the fastest way to stop a generated grid reading as graph paper.
                float rowShift = (gz % 2 == 0 ? 1f : -1f) * pitch * RowStagger * 0.5f;
                float columnShift = (gx % 2 == 0 ? -1f : 1f) * pitch * RowStagger * 0.35f;

                var origin = new Vector3(
                    (gx - (GridSize - 1) * 0.5f) * pitch + rowShift,
                    0f,
                    (gz - (GridSize - 1) * 0.5f) * pitch + columnShift);

                if (isCentre)
                {
                    // The keep stays square to the world and centred. It is the one building the
                    // town was laid out around, and a crooked castle reads as a mistake.
                    var keepAt = new Vector3(
                        (gx - (GridSize - 1) * 0.5f) * pitch,
                        0f,
                        (gz - (GridSize - 1) * 0.5f) * pitch);

                    // The moat first: it decides how high the keep stands, because the castle is
                    // built on the island rather than dropped in beside it.
                    float plinth = BuildCastleLake(keepAt);
                    BuildKeep(keepAt + Vector3.up * plinth);
                    continue;
                }

                // A few cells are simply left open: a market square, a shrine yard, a gap where
                // something burned down. Irregular open space does as much for the feel of a place
                // as irregular buildings.
                if (_random.NextDouble() < PlazaChance)
                {
                    _sketch?.Add(MapSketch.Feature.Plaza, origin,
                        new Vector2(BlockSize * 0.5f, BlockSize * 0.5f));

                    double what = _random.NextDouble();
                    int plazaSalt = gx * 31 + gz;

                    if (what < ZenGardenChance) BuildZenGarden(origin, plazaSalt);
                    else if (what < ZenGardenChance + RockGardenChance)
                        BuildRockGarden(origin, plazaSalt);
                    else if (what < ZenGardenChance + RockGardenChance + ShrineChance)
                        BuildShrine(origin, plazaSalt);

                    continue;
                }

                origin += new Vector3(
                    (float)(_random.NextDouble() * 2f - 1f) * BlockJitter,
                    0f,
                    (float)(_random.NextDouble() * 2f - 1f) * BlockJitter);

                float turn = (float)(_random.NextDouble() * 2f - 1f) * BlockRotation;

                // Which kind of building stands here. A castle town was not built to one plan:
                // storehouses, terraces, gardens and shrines sat between the walled compounds, and
                // a street of nothing but identical courtyard houses is the tell that a place was
                // generated rather than grown.
                double roll = _random.NextDouble();
                int salt = gx * 31 + gz;

                if (roll < PagodaChance) BuildPagoda(origin, salt, turn);
                else if (roll < PagodaChance + KuraChance) BuildKura(origin, salt, turn);
                else if (roll < PagodaChance + KuraChance + NagayaChance) BuildNagaya(origin, salt, turn);
                else if (roll < PagodaChance + KuraChance + NagayaChance + TeahouseChance)
                    BuildTeahouseGarden(origin, salt, turn);
                else BuildCompound(origin, salt, turn);
            }

            if (_riverColumn >= 0) BuildRiverChannel(extent, pitch);
            if (BuildSewers) BuildSewerNetwork(extent, pitch);
            BuildStreetLanterns(extent, pitch);
            BuildStreetFurniture(extent, pitch);
            BuildVerges(extent, pitch);
            BuildHedges(extent, pitch);
            BuildTownMist(extent, pitch);
            BuildFoliage(extent, pitch);
            BuildRampart(extent);
            BuildSpiritForest();
            BudgetLanternLights();
            CombineStatics();

            _sketch.Extent = extent;

            MapDescriptor descriptor = gameObject.GetComponent<MapDescriptor>();
            if (descriptor == null) descriptor = gameObject.AddComponent<MapDescriptor>();
            descriptor.Center = Vector3.zero;
            // The playable radius is the rampart, not an estimate from the grid: the mist, the bot
            // patrol picker and the bounds clamp all read this, and they should all agree with the
            // wall the player can actually see.
            descriptor.Radius = _rampartRing > 0f ? _rampartRing + 1f : extent * 1.15f;

            // The rampart is four straight walls, not a ring, and the bounds clamp has to agree
            // with it or the corners of the town become unreachable.
            descriptor.HalfExtent = _rampartRing > 0f ? _rampartRing : 0f;
            descriptor.FloorY = -SewerDepth - 2f;
            descriptor.CeilingY = WallHeight + SecondStoreyHeight + 12f;

            Debug.Log($"[Unseen] greybox town generated: {ShojiPanel.All.Count} shoji, " +
                      $"{Lantern.All.Count} lanterns, {LootContainer.All.Count} containers, " +
                      $"radius {descriptor.Radius:0} m, " +
                      $"{(_textured ? "textured" : "flat greybox")}, " +
                      $"{BoxMeshFactory.CachedMeshCount} box meshes, " +
                      $"{_root.GetComponentsInChildren<Renderer>(true).Length} renderers, " +
                      $"{_root.GetComponentsInChildren<Collider>(true).Length} colliders, " +
                      $"{_pagodas} pagodas, {_kura} kura, {_nagaya} nagaya, " +
                      $"{_gardens} gardens, {_shrines} shrines, {_trees} trees, {_shrubs} shrubs, " +
                      $"{_zenGardens} zen gardens, {_rockGardens} rock gardens, " +
                      $"{_waterfalls} waterfalls, {_koi} koi, " +
                      $"{_birds} birds, {_animals} animals, " +
                      $"{_stoodLanterns} lanterns stood on posts, {_unhungLanterns} skipped, " +
                      $"river={(_riverColumn >= 0 ? "yes" : "no")}, " +
                      $"{(_sketch != null ? _sketch.Landmarks.Count : 0)} map landmarks");

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
        private void BuildCompound(Vector3 origin, int salt, float turn = 0f)
        {
            var compound = new GameObject($"Compound_{salt}").transform;
            compound.SetParent(_root, false);
            compound.localPosition = origin;

            // Everything below is built in the compound's local space, so turning the parent turns
            // the walls, the shoji, the rafters, the roof and the colliders together.
            compound.localRotation = Quaternion.Euler(0f, turn, 0f);

            // Plot sizes vary, which is what actually widens one street and pinches the next -
            // but only across a few discrete sizes.
            //
            // A continuously random size gives every compound in the town its own dimensions, and
            // BoxMeshFactory caches by dimension: the first version of this took the cache from
            // 603 meshes to 5,598. Three sizes read as varied and share their geometry.
            int step = _random.Next(BlockSizeSteps);
            float scale = Mathf.Lerp(MinBlockScale, 1f, BlockSizeSteps <= 1 ? 1f : step / (float)(BlockSizeSteps - 1));
            float blockSize = BlockSize * scale;

            _sketch?.Add(MapSketch.Feature.Block, origin,
                new Vector2(blockSize * 0.5f, blockSize * 0.5f));

            float half = blockSize * 0.5f;
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
                    ? new Vector3(blockSize, height, 0.4f)
                    : new Vector3(0.4f, height, blockSize);

                if (side == doorSide)
                {
                    // Split the wall to leave a 3 m doorway in the middle.
                    float segment = (blockSize - 3f) * 0.5f;
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
                new Vector3(blockSize - 1f, 0.1f, blockSize - 1f), UnseenLayers.Default, _tatami);
            Acoustics(floor, 0.6f, 0.55f, 0.6f);

            BuildShojiRun(compound, new Vector3(0f, WallHeight * 0.5f, 0f), blockSize - 2f, true, WallHeight);
            BuildShojiRun(compound, new Vector3(0f, WallHeight * 0.5f, 0f), blockSize - 2f, false, WallHeight);

            // Rafters under the roof: the classic overhead ambush lane.
            int rafters = 3;
            for (int i = 0; i < rafters; i++)
            {
                float t = (i + 1f) / (rafters + 1f);
                float z = Mathf.Lerp(-half + 2f, half - 2f, t);
                Transform beam = Box(compound, $"Rafter_{i}",
                    new Vector3(0f, height - 0.6f, z),
                    new Vector3(blockSize - 1.5f, 0.3f, 0.5f),
                    UnseenLayers.Rafter, _rafter);
                Acoustics(beam, 0.3f, 0.4f, 0.5f);
            }

            float roofTop = BuildHipRoof(compound, blockSize + EaveOverhang * 2f, height);

            Transform ridge = Box(compound, "Ridge",
                new Vector3(0f, roofTop + 0.5f, 0f),
                new Vector3(blockSize * 0.6f, 1f, 1.2f),
                UnseenLayers.GrappleAnchor, _tile);
            Acoustics(ridge, 0.5f, 1.2f, 1.3f);

            if (twoStorey)
            {
                Transform midFloor = Box(compound, "UpperFloor",
                    new Vector3(0f, WallHeight, 0f),
                    new Vector3(blockSize - 3f, 0.3f, blockSize - 3f),
                    UnseenLayers.Default, _woodFloor);
                Acoustics(midFloor, 0.65f, 0.7f, 0.8f);
            }

            BuildEngawa(compound, half, doorSide);
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

        // ---------------------------------------------------------------- other buildings

        /// <summary>
        /// A kura: the fireproof storehouse a merchant kept his stock in.
        ///
        /// Thick white plaster over a timber frame, almost no openings, and a heavy tiled roof.
        /// Blank, tall and windowless, which makes it the one building on a street with nothing to
        /// climb and nothing to see through - useful cover to move behind and a dead end to be
        /// caught against.
        /// </summary>
        private void BuildKura(Vector3 origin, int salt, float turn)
        {
            var kura = new GameObject($"Kura_{salt}").transform;
            kura.SetParent(_root, false);
            kura.localPosition = origin;
            kura.localRotation = Quaternion.Euler(0f, turn, 0f);
            _kura++;

            float size = BlockSize * (0.4f + (float)_random.NextDouble() * 0.12f);
            float height = 7.5f + (float)_random.NextDouble() * 2.5f;
            float half = size * 0.5f;

            _sketch?.Add(MapSketch.Feature.Store, origin, new Vector2(half, half));

            Transform plinth = Box(kura, "Plinth", new Vector3(0f, 0.35f, 0f),
                new Vector3(size + 1.2f, 0.7f, size + 1.2f), UnseenLayers.Default, _stone);
            Acoustics(plinth, 0.9f, 1.1f, 1.1f);

            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;

                Transform wall = Box(kura, $"Wall_{side}",
                    horizontal
                        ? new Vector3(0f, 0.7f + height * 0.5f, half * sign)
                        : new Vector3(half * sign, 0.7f + height * 0.5f, 0f),
                    horizontal
                        ? new Vector3(size, height, 0.7f)
                        : new Vector3(0.7f, height, size),
                    UnseenLayers.Occluder, _plaster);
                Acoustics(wall, 0.95f, 1f, 1f);

                // One small barred opening high up, and a band of dark plaster at the foot: the
                // two details that make a kura read as a kura rather than a white box.
                Detail(kura, $"Skirt_{side}",
                    horizontal
                        ? new Vector3(0f, 1.5f, (half + 0.12f) * sign)
                        : new Vector3((half + 0.12f) * sign, 1.5f, 0f),
                    horizontal ? new Vector3(size, 1.6f, 0.2f) : new Vector3(0.2f, 1.6f, size),
                    _darkTimber);

                Detail(kura, $"Vent_{side}",
                    horizontal
                        ? new Vector3(0f, 0.7f + height * 0.72f, (half + 0.12f) * sign)
                        : new Vector3((half + 0.12f) * sign, 0.7f + height * 0.72f, 0f),
                    horizontal ? new Vector3(1.4f, 0.9f, 0.2f) : new Vector3(0.2f, 0.9f, 1.4f),
                    _darkTimber);
            }

            Transform floor = Box(kura, "Floor", new Vector3(0f, 0.75f, 0f),
                new Vector3(size - 1f, 0.2f, size - 1f), UnseenLayers.Default, _woodFloor);
            Acoustics(floor, 0.6f, 0.7f, 0.8f);

            float roofTop = BuildHipRoof(kura, size + EaveOverhang * 1.4f, 0.7f + height);

            Transform crest = Box(kura, "Ridge", new Vector3(0f, roofTop + 0.5f, 0f),
                new Vector3(size * 0.5f, 0.9f, 1f), UnseenLayers.GrappleAnchor, _tile);
            Acoustics(crest, 0.5f, 1.2f, 1.3f);

            PlaceContainers(kura, half * 0.6f);
        }

        /// <summary>
        /// A nagaya: the long low terrace ordinary townspeople lived in, one room per family with
        /// a door straight onto the street.
        ///
        /// Low and deep rather than square, so it breaks the rhythm of walled compounds, and its
        /// roof is a single long run - the easiest roof in the town to travel along and the most
        /// exposed while you do.
        /// </summary>
        private void BuildNagaya(Vector3 origin, int salt, float turn)
        {
            var row = new GameObject($"Nagaya_{salt}").transform;
            row.SetParent(_root, false);
            row.localPosition = origin;
            row.localRotation = Quaternion.Euler(0f, turn, 0f);
            _nagaya++;

            float length = BlockSize * (0.78f + (float)_random.NextDouble() * 0.12f);
            float depth = BlockSize * 0.3f;
            const float height = 3.6f;

            _sketch?.Add(MapSketch.Feature.Row, origin, new Vector2(length * 0.5f, depth * 0.5f));

            Transform plinth = Box(row, "Plinth", new Vector3(0f, 0.25f, 0f),
                new Vector3(length + 0.8f, 0.5f, depth + 0.8f), UnseenLayers.Default, _stone);
            Acoustics(plinth, 0.9f, 1.1f, 1.1f);

            Transform back = Box(row, "Back", new Vector3(0f, 0.5f + height * 0.5f, -depth * 0.5f),
                new Vector3(length, height, 0.35f), UnseenLayers.Occluder, _plaster);
            Acoustics(back, 0.75f, 1f, 1f);

            for (int side = -1; side <= 1; side += 2)
            {
                Transform end = Box(row, $"End_{side}",
                    new Vector3(length * 0.5f * side, 0.5f + height * 0.5f, 0f),
                    new Vector3(0.35f, height, depth), UnseenLayers.Occluder, _plaster);
                Acoustics(end, 0.75f, 1f, 1f);
            }

            // The street face: a run of doorways with a post between each, which is the whole
            // character of a nagaya.
            int doors = Mathf.Max(3, Mathf.RoundToInt(length / 4.5f));
            float bay = length / doors;

            for (int i = 0; i <= doors; i++)
            {
                float x = -length * 0.5f + bay * i;
                Transform post = Box(row, $"Post_{i}", new Vector3(x, 0.5f + height * 0.5f, depth * 0.5f),
                    new Vector3(0.32f, height, 0.4f), UnseenLayers.Occluder, _darkTimber);
                Acoustics(post, 0.6f, 1f, 1f);

                if (i >= doors) continue;

                float centre = x + bay * 0.5f;

                Detail(row, $"Lintel_{i}", new Vector3(centre, 0.5f + height - 0.3f, depth * 0.5f + 0.1f),
                    new Vector3(bay, 0.34f, 0.3f), _darkTimber);

                Detail(row, $"Noren_{i}", new Vector3(centre, 0.5f + height - 1.1f, depth * 0.5f + 0.16f),
                    new Vector3(bay * 0.7f, 1.1f, 0.06f), _paper);

                if (i % 2 == 0)
                    MountLantern(row, new Vector3(x + 0.2f, 0.5f + height - 0.5f, depth * 0.5f),
                        Vector3.forward, 9f, 0.9f);
            }

            Transform floor = Box(row, "Floor", new Vector3(0f, 0.55f, 0f),
                new Vector3(length - 0.7f, 0.2f, depth - 0.7f), UnseenLayers.Default, _tatami);
            Acoustics(floor, 0.6f, 0.55f, 0.6f);

            BuildShojiRun(row, new Vector3(0f, 0.5f + height * 0.5f, 0f), length - 2f, true, height);
            BuildHipRoof(row, depth + EaveOverhang * 2f, 0.5f + height);
            PlaceContainers(row, depth * 0.3f);
        }

        /// <summary>
        /// A walled garden with a teahouse in the corner of it.
        ///
        /// Mostly open ground behind a low wall, which a town of walled compounds badly needs: it
        /// is the only block type here that gives a sightline through the middle of a plot, and the
        /// only one where a lantern lights something worth looking at.
        /// </summary>
        private void BuildTeahouseGarden(Vector3 origin, int salt, float turn)
        {
            var garden = new GameObject($"Garden_{salt}").transform;
            garden.SetParent(_root, false);
            garden.localPosition = origin;
            garden.localRotation = Quaternion.Euler(0f, turn, 0f);
            _gardens++;

            float half = BlockSize * 0.45f;
            _sketch?.Add(MapSketch.Feature.Garden, origin, new Vector2(half, half));

            // A low wall, so the garden is enclosed but not blind.
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                if (side == salt % 4) continue; // one open side, the way in

                Transform wall = Box(garden, $"Wall_{side}",
                    horizontal ? new Vector3(0f, 1.1f, half * sign) : new Vector3(half * sign, 1.1f, 0f),
                    horizontal ? new Vector3(half * 2f, 2.2f, 0.4f) : new Vector3(0.4f, 2.2f, half * 2f),
                    UnseenLayers.Occluder, _plaster);
                Acoustics(wall, 0.75f, 1f, 1f);

                Detail(garden, $"Coping_{side}",
                    horizontal ? new Vector3(0f, 2.3f, half * sign) : new Vector3(half * sign, 2.3f, 0f),
                    horizontal ? new Vector3(half * 2f, 0.2f, 0.7f) : new Vector3(0.7f, 0.2f, half * 2f),
                    _tile);
            }

            // The teahouse itself: small, low, and set into one corner.
            float hutHalf = 4.2f;
            var hut = new Vector3(half * 0.42f, 0f, -half * 0.42f);

            Transform deck = Box(garden, "TeahouseDeck", hut + new Vector3(0f, 0.45f, 0f),
                new Vector3(hutHalf * 2f + 1.4f, 0.9f, hutHalf * 2f + 1.4f),
                UnseenLayers.Default, _woodFloor);
            Acoustics(deck, 0.55f, 0.8f, 0.9f);

            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                if (side == 0) continue; // open to the garden

                Transform wall = Box(garden, $"HutWall_{side}",
                    hut + (horizontal
                        ? new Vector3(0f, 0.9f + 1.3f, hutHalf * sign)
                        : new Vector3(hutHalf * sign, 0.9f + 1.3f, 0f)),
                    horizontal
                        ? new Vector3(hutHalf * 2f, 2.6f, 0.3f)
                        : new Vector3(0.3f, 2.6f, hutHalf * 2f),
                    UnseenLayers.Occluder, _plaster);
                Acoustics(wall, 0.7f, 1f, 1f);
            }

            BuildShojiRun(garden, hut + new Vector3(0f, 2.1f, 0f), hutHalf * 1.6f, true, 2.6f);
            BuildHipRoof(garden, hutHalf * 2f + 3f, 3.5f);

            // A stone lantern on the path, a few stepping stones and some planting. This is the
            // one block in the town where the decoration IS the building.
            Transform pedestal = Box(garden, "StoneLantern", new Vector3(-half * 0.3f, 0.8f, half * 0.25f),
                new Vector3(0.5f, 1.6f, 0.5f), UnseenLayers.Occluder, _stone);
            Acoustics(pedestal, 0.9f, 1f, 1f);
            CreateLantern(garden, new Vector3(-half * 0.3f, 1.9f, half * 0.25f), 9f, 0.9f);

            for (int i = 0; i < 7; i++)
            {
                float t = i / 6f;
                var at = new Vector3(Mathf.Lerp(-half * 0.7f, hut.x, t),
                    0.08f, Mathf.Lerp(half * 0.7f, hut.z + hutHalf, t));
                Detail(garden, $"Stone_{i}", at, new Vector3(0.8f, 0.16f, 0.7f), _stone);
            }

            for (int i = 0; i < 5; i++)
            {
                var at = new Vector3(
                    (float)(_random.NextDouble() * 2f - 1f) * half * 0.7f, 0f,
                    (float)(_random.NextDouble() * 2f - 1f) * half * 0.7f);
                BuildShrub(garden, at);
            }
        }

        /// <summary>
        /// A small shrine with a torii gate in front of it, standing in an open plaza.
        ///
        /// The torii is the most recognisable silhouette in the whole town and costs six boxes.
        /// </summary>
        private void BuildShrine(Vector3 origin, int salt)
        {
            var shrine = new GameObject($"Shrine_{salt}").transform;
            shrine.SetParent(_root, false);
            shrine.localPosition = origin;
            shrine.localRotation = Quaternion.Euler(0f, (salt % 4) * 90f, 0f);
            _shrines++;

            _sketch?.Add(MapSketch.Feature.Shrine, origin, new Vector2(6f, 6f));

            // Torii: two pillars, a curved-looking lintel of two beams, and a tie bar.
            const float gateHalf = 3.2f;
            const float gateHeight = 5.4f;

            for (int side = -1; side <= 1; side += 2)
            {
                Transform pillar = Box(shrine, $"ToriiPillar_{side}",
                    new Vector3(gateHalf * side, gateHeight * 0.5f, 8f),
                    new Vector3(0.55f, gateHeight, 0.55f), UnseenLayers.Occluder, _vermilion);
                Acoustics(pillar, 0.6f, 1f, 1f);
            }

            Detail(shrine, "ToriiKasagi", new Vector3(0f, gateHeight + 0.35f, 8f),
                new Vector3(gateHalf * 2f + 2.2f, 0.42f, 0.8f), _vermilion);
            Detail(shrine, "ToriiShimagi", new Vector3(0f, gateHeight - 0.15f, 8f),
                new Vector3(gateHalf * 2f + 1.2f, 0.3f, 0.6f), _vermilion);
            Detail(shrine, "ToriiNuki", new Vector3(0f, gateHeight * 0.72f, 8f),
                new Vector3(gateHalf * 2f + 0.6f, 0.28f, 0.42f), _vermilion);
            Detail(shrine, "ToriiGakuzuka", new Vector3(0f, gateHeight * 0.86f, 8f),
                new Vector3(0.3f, 1.1f, 0.34f), _vermilion);

            // The hall behind it, raised on a stone platform.
            Transform platform = Box(shrine, "Platform", new Vector3(0f, 0.55f, -2f),
                new Vector3(11f, 1.1f, 9f), UnseenLayers.Default, _stone);
            Acoustics(platform, 0.9f, 1.1f, 1.1f);

            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                if (side == 0) continue; // open front, facing the gate

                Transform wall = Box(shrine, $"HallWall_{side}",
                    new Vector3(0f, 0f, -2f) + (horizontal
                        ? new Vector3(0f, 1.1f + 1.7f, 4f * sign)
                        : new Vector3(5f * sign, 1.1f + 1.7f, 0f)),
                    horizontal ? new Vector3(10f, 3.4f, 0.4f) : new Vector3(0.4f, 3.4f, 8f),
                    UnseenLayers.Occluder, _plaster);
                Acoustics(wall, 0.8f, 1f, 1f);
            }

            for (int side = -1; side <= 1; side += 2)
                Detail(shrine, $"HallPost_{side}", new Vector3(4.6f * side, 2.8f, 2f),
                    new Vector3(0.42f, 3.4f, 0.42f), _vermilion);

            BuildHipRoof(shrine, 13f, 4.5f);

            for (int side = -1; side <= 1; side += 2)
                HangLantern(shrine, new Vector3(3.6f * side, 3.6f, 2.2f), 10f, 1f);

            PlaceContainers(shrine, 3f);
        }

        // ---------------------------------------------------------------- river        // ---------------------------------------------------------------- river

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

            _sketch?.Add(MapSketch.Feature.Water, new Vector3(_riverCentreX, 0f, 0f),
                new Vector2(RiverWidth * 0.5f, extent + StreetWidth * 0.5f));

            float length = extent * 2f + StreetWidth;
            float channelHalf = (BlockSize + StreetWidth) * 0.5f;
            float towpath = channelHalf - RiverWidth * 0.5f;
            float bedY = -RiverDepth;

            Transform bed = Box(river, "Bed", new Vector3(_riverCentreX, bedY - 0.3f, 0f),
                new Vector3(RiverWidth, 0.6f, length), UnseenLayers.Default, _stone);
            Acoustics(bed, 0.9f, 1.3f, 1.4f);

            // The channel runs full.
            //
            // The water used to sit two centimetres proud of the towpaths, so the river read as a
            // damp floor with kerbs. It now fills most of the channel and the paths are a dry ledge
            // above it: crossing means stepping down into the water, which is the whole reason the
            // river is a risk worth routing around.
            float waterTop = bedY + WaterDepth;
            float pathTop = waterTop + TowpathFreeboard;

            // The surface is NOT a floor. It used to be an ordinary collider, which meant a
            // player walked across the river with dry feet and the whole channel was a bridge.
            // What you stand on is the bed; this is only the thing you look at.
            // A thin surface rather than a block of water.
            //
            // It used to be a box as deep as the channel, which was harmless while it was also the
            // collider and disastrous once it stopped being one: crouch or go prone in the deep
            // middle and the camera ends up INSIDE the box, where every face is back-facing and the
            // whole river is culled away. The water simply vanished from certain angles.
            //
            // The thickness was never doing anything. What is wanted is the surface.
            const float surfaceThickness = 0.16f;

            Transform water = Detail(river, "Water",
                new Vector3(_riverCentreX, waterTop - surfaceThickness * 0.5f, 0f),
                new Vector3(RiverWidth, surfaceThickness, length), _water);

            water.gameObject.AddComponent<WaterVolume>().Configure(
                waterTop, new Vector2(RiverWidth * 0.5f, length * 0.5f), WadeDeep + 0.2f);

            // Tell the water shader the shape of the bed underneath it.
            //
            // It colours and thins itself by depth - pale and see-through over the shelves where
            // the gravel shows, dark and solid down the middle - and it has no other way to know
            // where the steps are. The generator dug them, so the generator says.
            //
            // Set on the shared material rather than through a property block: there is exactly one
            // river, and a block would cost a separate draw for no benefit.
            if (_water != null && _water.HasProperty("_ChannelCentre"))
            {
                _water.SetFloat("_ChannelCentre", _riverCentreX);
                _water.SetFloat("_ChannelHalf", RiverWidth * 0.5f);
                _water.SetFloat("_DeepHalf", RiverWidth * 0.25f);
                _water.SetFloat("_ShallowDepth", WadeShallow);
                _water.SetFloat("_DeepDepth", WadeDeep);
            }

            // The bottom you actually walk on: shallow shelves either side and a deeper channel
            // down the middle. A river of one uniform depth is a trench; this one you can cross at
            // the edges and hide in at the centre, and crouching in the middle puts your head
            // under the surface.
            BuildRiverBed(river, length, waterTop, bedY);

            // Sound and surface motion. Both are driven from one component so the emitters and the
            // scroll cannot disagree about where the river is.
            var ambience = river.gameObject.AddComponent<RiverAmbience>();
            ambience.Configure(_riverCentreX, length * 0.5f, _water);

            for (int side = -1; side <= 1; side += 2)
            {
                float pathCentre = _riverCentreX + side * (RiverWidth * 0.5f + towpath * 0.5f);

                // The ledge, standing clear of the water rather than under it.
                float pathThickness = pathTop - bedY;
                Transform path = Box(river, $"Towpath_{side}",
                    new Vector3(pathCentre, bedY + pathThickness * 0.5f, 0f),
                    new Vector3(towpath, pathThickness, length), UnseenLayers.Default, _stone);
                Acoustics(path, 0.85f, 1.1f, 1.15f);

                float wallX = _riverCentreX + side * channelHalf;
                Transform wall = Box(river, $"Embankment_{side}",
                    new Vector3(wallX, bedY * 0.5f, 0f),
                    new Vector3(0.8f, RiverDepth + 0.4f, length), UnseenLayers.Occluder, _stone);
                Acoustics(wall, 0.95f, 1f, 1f);
            }

            BuildRiverDressing(river, length, channelHalf, bedY, waterTop, pathTop);
            BuildRiverStairs(river, extent, pitch, channelHalf, bedY);
            BuildBridges(river, extent, pitch, channelHalf, bedY);
        }

        /// <summary>
        /// The bed of the river: what a wading player actually stands on.
        ///
        /// Three slabs rather than one flat bottom. The shelves along each bank are waist deep, so
        /// crossing is possible but slow and loud; the middle is deep enough that a standing ninja
        /// is in it to the chest and a crouching one is under it. That difference is the whole
        /// reason to be in the river rather than on the bridge above it.
        ///
        /// All three carry the water acoustic profile, which is what makes footsteps here splash:
        /// the footstep sound is chosen from the surface underfoot, and underfoot is the bed.
        /// </summary>
        private void BuildRiverBed(Transform river, float length, float waterTop, float bedY)
        {
            float shelfTop = waterTop - WadeShallow;
            float channelTop = waterTop - WadeDeep;

            float channelWidth = RiverWidth * 0.5f;
            float shelfWidth = (RiverWidth - channelWidth) * 0.5f;

            Transform channel = Box(river, "RiverbedChannel",
                new Vector3(_riverCentreX, (bedY + channelTop) * 0.5f, 0f),
                new Vector3(channelWidth, channelTop - bedY, length),
                UnseenLayers.Default, _riverStone);
            Acoustics(channel, 0.2f, 2.2f, 2.4f);

            for (int side = -1; side <= 1; side += 2)
            {
                float centre = _riverCentreX + side * (channelWidth + shelfWidth) * 0.5f;

                Transform shelf = Box(river, $"RiverbedShelf_{side}",
                    new Vector3(centre, (bedY + shelfTop) * 0.5f, 0f),
                    new Vector3(shelfWidth, shelfTop - bedY, length),
                    UnseenLayers.Default, _riverStone);
                Acoustics(shelf, 0.2f, 2.2f, 2.4f);
            }

            Debug.Log($"[Unseen] riverbed: {WadeShallow:0.00} m over the shelves, " +
                      $"{WadeDeep:0.00} m down the middle");
        }

        /// <summary>
        /// Rocks, reeds and mooring posts along the channel.
        ///
        /// The river was a blue slab between two grey slabs. These are what make it read as a
        /// watercourse: something breaking the surface, something growing at the margin, and
        /// something built by people at the edge.
        /// </summary>
        private void BuildRiverDressing(Transform river, float length, float channelHalf,
            float bedY, float waterTop, float pathTop)
        {
            int rocks = Mathf.RoundToInt(length / 22f);

            for (int i = 0; i < rocks; i++)
            {
                float z = Mathf.Lerp(-length * 0.45f, length * 0.45f, i / (float)Mathf.Max(1, rocks - 1));
                z += (float)(_random.NextDouble() - 0.5) * 12f;

                float x = _riverCentreX + (float)(_random.NextDouble() * 2f - 1f) * RiverWidth * 0.38f;
                float size = 0.8f + (float)_random.NextDouble() * 1.5f;

                // Mostly under. A boulder standing a metre clear of the water reads as a crate
                // somebody dropped in the river; one with just its crown showing reads as a rock
                // the water has been running over for a century.
                Transform rock = Box(river, $"Rock_{i}",
                    new Vector3(x, waterTop - size * 0.62f, z),
                    new Vector3(size, size, size * 0.8f), UnseenLayers.Occluder, null);
                rock.localRotation = Quaternion.Euler(
                    (float)_random.NextDouble() * 22f,
                    (float)_random.NextDouble() * 360f,
                    (float)_random.NextDouble() * 22f);
                Acoustics(rock, 0.9f, 1f, 1f);

                MeshRenderer rockBox = rock.GetComponent<MeshRenderer>();
                if (rockBox != null) rockBox.enabled = false;

                // A boulder is a lumpy thing. A rotated cube is a rotated cube.
                Organic(rock, "Stone", OrganicMeshFactory.Blob(6, 10, 0.38f, i % 8),
                    Vector3.zero, Vector3.one, _riverStone);

                // Two smaller ones alongside, so a rock is an outcrop rather than a single object.
                for (int c = 0; c < 2; c++)
                {
                    float small = size * (0.35f + (float)_random.NextDouble() * 0.3f);
                    Transform chip = Organic(river, $"Rock_{i}_{c}",
                        OrganicMeshFactory.Blob(5, 8, 0.42f, (i + c) % 8),
                        new Vector3(x + (float)(_random.NextDouble() * 2f - 1f) * size,
                            waterTop - small * 0.55f,
                            z + (float)(_random.NextDouble() * 2f - 1f) * size),
                        new Vector3(small, small, small * 0.8f), _riverStone);
                    chip.localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);
                }
            }

            // The embankment face: courses of stone, and a band of moss along the waterline.
            //
            // A four-hundred-metre wall of one flat texture is the single least convincing thing in
            // a river, and the waterline is where the eye goes to judge whether water is real. Moss
            // grows exactly where the stone is permanently damp and nowhere else, so a band of it
            // at that height does more than any amount of surface detail higher up.
            for (int side = -1; side <= 1; side += 2)
            {
                float face = _riverCentreX + side * (RiverWidth * 0.5f + 0.05f);

                int bands = Mathf.RoundToInt(length / 14f);
                for (int i = 0; i < bands; i++)
                {
                    float z = Mathf.Lerp(-length * 0.49f, length * 0.49f, i / (float)Mathf.Max(1, bands - 1));
                    float run = length / bands * 0.94f;

                    // Damp stone right at the water, thinning as it dries upward.
                    Detail(river, $"Moss_{side}_{i}",
                        new Vector3(face, waterTop + 0.2f, z),
                        new Vector3(0.16f, 0.65f + (float)_random.NextDouble() * 0.4f, run),
                        _moss);

                    // One course of masonry above it, in the bank's OWN stone and barely proud of
                    // the face.
                    //
                    // Two courses of the wet river-stone map projecting six centimetres out read as
                    // black beams bolted to the embankment - that material is now a genuinely dark
                    // wet cobble, and against pale dry masonry it is nearly a silhouette. A course
                    // is a joint line, not a shelf.
                    float y = waterTop + 1.05f;
                    float stagger = i % 2 == 0 ? 0.4f : -0.4f;

                    Detail(river, $"Course_{side}_{i}",
                        new Vector3(face + side * 0.02f, y, z + stagger),
                        new Vector3(0.07f, 0.16f, run * 0.9f), _stone);
                }
            }

            for (int side = -1; side <= 1; side += 2)
            {
                float margin = _riverCentreX + side * (RiverWidth * 0.5f - 0.4f);
                float postLine = _riverCentreX + side * (RiverWidth * 0.5f + 0.9f);

                int clumps = Mathf.RoundToInt(length / 9f);
                for (int i = 0; i < clumps; i++)
                {
                    float z = Mathf.Lerp(-length * 0.48f, length * 0.48f, i / (float)Mathf.Max(1, clumps - 1));
                    if (_random.NextDouble() < 0.3) continue;

                    // Reeds at the waterline. A single box of foliage reads as a green crate
                    // floating in the river; what reads as reeds is several thin blades of
                    // different heights leaning away from each other.
                    var root = new Vector3(margin + (float)(_random.NextDouble() - 0.5) * 1.2f,
                        0f, z);

                    int blades = 4 + _random.Next(4);
                    for (int b = 0; b < blades; b++)
                    {
                        float tall = 1.0f + (float)_random.NextDouble() * 1.3f;
                        var at = root + new Vector3(
                            (float)(_random.NextDouble() * 2f - 1f) * 0.45f,
                            waterTop + tall * 0.42f,
                            (float)(_random.NextDouble() * 2f - 1f) * 0.45f);

                        Transform blade = Organic(river, $"Reed_{side}_{i}_{b}",
                            OrganicMeshFactory.Blade(4, 0.3f),
                            at - new Vector3(0f, tall * 0.42f, 0f),
                            new Vector3(0.09f, tall, 1f), _reed);
                        blade.localRotation = Quaternion.Euler(
                            (float)(_random.NextDouble() * 2f - 1f) * 16f,
                            (float)_random.NextDouble() * 90f,
                            (float)(_random.NextDouble() * 2f - 1f) * 16f);
                    }
                }

                int posts = Mathf.RoundToInt(length / 26f);
                for (int i = 0; i < posts; i++)
                {
                    float z = Mathf.Lerp(-length * 0.42f, length * 0.42f, i / (float)Mathf.Max(1, posts - 1));

                    // Mooring posts, standing on the ledge and leaning over the water.
                    Transform post = Box(river, $"Mooring_{side}_{i}",
                        new Vector3(postLine, pathTop + 0.7f, z),
                        new Vector3(0.28f, 1.4f, 0.28f), UnseenLayers.Occluder, _darkTimber);
                    post.localRotation = Quaternion.Euler(0f, 0f, side * 5f);
                    Acoustics(post, 0.5f, 1f, 1f);

                    Detail(river, $"MooringCap_{side}_{i}",
                        new Vector3(postLine, pathTop + 1.44f, z),
                        new Vector3(0.38f, 0.12f, 0.38f), _stone);
                }
            }
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
                        float top = Mathf.Lerp(0.1f, bedY + WaterDepth + TowpathFreeboard + 0.5f, t);
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

                _sketch?.Add(MapSketch.Feature.Bridge, new Vector3(_riverCentreX, 0f, z),
                    new Vector2(channelHalf + 1.5f, 4.5f));

                float span = channelHalf * 2f + 3f;
                const float deckWidth = 9f;
                const float deckY = 0.55f;

                // A taiko-bashi: an arched drum bridge rather than a plank.
                //
                // Built as stepped segments along the arc, each riser kept under the character
                // controller's step offset so the bridge is walked over rather than bumped into.
                // A true curved mesh would need its own collider and its own generator; a
                // staircase that follows a circle reads as one from every angle that matters.
                Transform deck = BuildArchedDeck(bridge, z, span, deckWidth, deckY);
                Acoustics(deck, 0.55f, 1.4f, 1.5f);

                // Piers, standing in the water and reaching the deck above them.
                //
                // Their height used to be RiverDepth, which was right when the deck was flat and
                // wrong the moment it arched: the crown rises 2.9 m and the piers stopped where the
                // old flat deck used to be, leaving them hanging in the water under a bridge they
                // were supposed to be holding up. Each one is measured against the arch directly
                // above it now.
                for (int p = -1; p <= 1; p += 2)
                {
                    float pierX = _riverCentreX + p * RiverWidth * 0.28f;

                    // Where this pier meets the span, as a fraction along it.
                    float t = Mathf.InverseLerp(_riverCentreX - span * 0.5f,
                        _riverCentreX + span * 0.5f, pierX);

                    // Up to the underside of the deck: the planks hang about half a metre below
                    // the walking surface, and the pier should meet timber rather than poke
                    // through it.
                    float underside = deckY + ArchHeight(t) - 0.45f;
                    float bottom = bedY - 0.3f;
                    float height = Mathf.Max(1f, underside - bottom);

                    Transform pier = Box(bridge, $"Pier_{p}",
                        new Vector3(pierX, bottom + height * 0.5f, z),
                        new Vector3(1.1f, height, 1.4f), UnseenLayers.Occluder, _stone);
                    Acoustics(pier, 0.9f, 1f, 1f);

                    // A capital where it meets the beam, so the join is a join and not two boxes
                    // ending at the same height.
                    Detail(bridge, $"PierCap_{p}",
                        new Vector3(pierX, underside + 0.12f, z),
                        new Vector3(1.5f, 0.24f, 1.8f), _darkTimber);
                }

                for (int r = -1; r <= 1; r += 2)
                {
                    float railZ = z + r * (deckWidth * 0.5f - 0.2f);

                    // The handrail follows the arc in short chords, and the balusters below it are
                    // vermilion - the one colour that reads as a shrine bridge at any distance.
                    const int chords = 12;
                    for (int c = 0; c < chords; c++)
                    {
                        float t0 = c / (float)chords;
                        float t1 = (c + 1) / (float)chords;

                        float x0 = Mathf.Lerp(_riverCentreX - span * 0.5f, _riverCentreX + span * 0.5f, t0);
                        float x1 = Mathf.Lerp(_riverCentreX - span * 0.5f, _riverCentreX + span * 0.5f, t1);
                        float y0 = deckY + ArchHeight(t0) + 1.05f;
                        float y1 = deckY + ArchHeight(t1) + 1.05f;

                        var mid = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, railZ);
                        float run = x1 - x0;
                        float rise = y1 - y0;
                        float len = Mathf.Sqrt(run * run + rise * rise);

                        Transform rail = Detail(bridge, $"Rail_{r}_{c}", mid,
                            new Vector3(len + 0.05f, 0.16f, 0.18f), _vermilion);
                        rail.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(rise, run) * Mathf.Rad2Deg);

                        float postX = x0 + run * 0.5f;
                        float postBase = deckY + ArchHeight(t0 + (t1 - t0) * 0.5f);
                        Detail(bridge, $"Baluster_{r}_{c}",
                            new Vector3(postX, postBase + 0.52f, railZ),
                            new Vector3(0.13f, 1.04f, 0.13f), _vermilion);
                    }

                    // Newel posts at each end and at the crown, each capped with a giboshi - the
                    // onion-shaped bronze finial that says at a glance this is a bridge somebody
                    // paid for rather than a plank across a ditch.
                    for (int n = -1; n <= 1; n++)
                    {
                        float t = 0.5f + n * 0.5f;
                        float x = Mathf.Lerp(_riverCentreX - span * 0.5f, _riverCentreX + span * 0.5f, t);
                        float baseY = deckY + ArchHeight(t);

                        Detail(bridge, $"Newel_{r}_{n}",
                            new Vector3(x, baseY + 1.25f, railZ),
                            new Vector3(0.34f, 2.5f, 0.34f), _vermilion);

                        // Giboshi: a stack of three shrinking blocks reads as the onion shape at
                        // any distance a player will ever see it from.
                        Detail(bridge, $"Giboshi_{r}_{n}_0",
                            new Vector3(x, baseY + 2.54f, railZ),
                            new Vector3(0.46f, 0.16f, 0.46f), _darkTimber);
                        Detail(bridge, $"Giboshi_{r}_{n}_1",
                            new Vector3(x, baseY + 2.74f, railZ),
                            new Vector3(0.38f, 0.28f, 0.38f), _darkTimber);
                        Detail(bridge, $"Giboshi_{r}_{n}_2",
                            new Vector3(x, baseY + 2.94f, railZ),
                            new Vector3(0.2f, 0.22f, 0.2f), _darkTimber);

                        if (n != 0)
                            CreateLantern(bridge, new Vector3(x, baseY + 2.05f, railZ), 12f, 1f);
                    }
                }

                // Cross-bracing under the arch, and the beams the deck sits on. From the towpath
                // this is most of what you see of a bridge, and it was previously nothing at all.
                for (int beam = -1; beam <= 1; beam += 2)
                {
                    for (int c = 0; c < 10; c++)
                    {
                        float t0 = c / 10f;
                        float t1 = (c + 1) / 10f;
                        float x0 = Mathf.Lerp(_riverCentreX - span * 0.5f, _riverCentreX + span * 0.5f, t0);
                        float x1 = Mathf.Lerp(_riverCentreX - span * 0.5f, _riverCentreX + span * 0.5f, t1);
                        float y0 = deckY + ArchHeight(t0) - 0.55f;
                        float y1 = deckY + ArchHeight(t1) - 0.55f;

                        float run = x1 - x0;
                        float rise = y1 - y0;
                        float len = Mathf.Sqrt(run * run + rise * rise);

                        Transform stringer = Detail(bridge, $"Stringer_{beam}_{c}",
                            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f,
                                z + beam * (deckWidth * 0.5f - 1.1f)),
                            new Vector3(len + 0.05f, 0.34f, 0.3f), _darkTimber);
                        stringer.localRotation =
                            Quaternion.Euler(0f, 0f, Mathf.Atan2(rise, run) * Mathf.Rad2Deg);
                    }
                }
            }
        }

        /// <summary>Rise of the bridge arch at a fraction along its span, in metres.</summary>
        private float ArchHeight(float t)
        {
            return Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI) * BridgeArchRise;
        }

        /// <summary>
        /// The deck of a drum bridge, as stepped segments following a sine arc.
        ///
        /// Every riser is kept under the character controller's 0.45 m step offset, so the bridge
        /// is walked over rather than climbed or blocked - the same constraint the tiered roofs
        /// are built to. The segment count is tied to the arch rise: the steepest riser is at the
        /// abutment, and a taller arch over the same count would put it over the limit.
        /// </summary>
        private Transform BuildArchedDeck(Transform bridge, float z, float span, float width, float baseY)
        {
            const int segments = 24;
            float length = span / segments;
            Transform first = null;

            for (int i = 0; i < segments; i++)
            {
                float t = (i + 0.5f) / segments;
                float x = Mathf.Lerp(_riverCentreX - span * 0.5f, _riverCentreX + span * 0.5f, t);
                float y = baseY + ArchHeight(t);

                // Each plank is thick enough to reach down to the one before it, so the underside
                // of the arch is solid and there is no gap to see - or fall - through.
                float sag = ArchHeight(t) - ArchHeight((i + (i < segments / 2 ? 1.5f : -0.5f)) / segments);
                float thickness = 0.5f + Mathf.Abs(sag);

                Transform plank = Box(bridge, $"Deck_{i}",
                    new Vector3(x, y - thickness * 0.5f + 0.25f, z),
                    new Vector3(length + 0.06f, thickness, width),
                    UnseenLayers.Default, _woodFloor);

                Acoustics(plank, 0.55f, 1.4f, 1.5f);
                if (first == null) first = plank;
            }

            return first;
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
        private void BuildPagoda(Vector3 origin, int salt, float turn = 0f)
        {
            var pagoda = new GameObject($"Pagoda_{salt}").transform;
            pagoda.SetParent(_root, false);
            pagoda.localPosition = origin;
            pagoda.localRotation = Quaternion.Euler(0f, turn, 0f);
            _pagodas++;

            _sketch?.Add(MapSketch.Feature.Pagoda, origin,
                new Vector2(BlockSize * 0.3f, BlockSize * 0.3f));

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

                if (i == 0) BuildCurvedEave(pagoda, size, y, $"Pagoda_{storey}_{i}");

                y += riser;
            }

            BuildRidge(pagoda, span - inset * 2f * (tiers - 1), y - riser + 0.15f,
                $"Pagoda_{storey}");

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

        /// <summary>
        /// The upturned corners of a roof, and the rafter ends under its edge.
        ///
        /// This is the single detail that separates a Japanese roof from a stack of slabs, and it
        /// is entirely in the corners: the eave line dips along each side and then flicks sharply
        /// UP at the corner, so the roof reads as a curve held at four points rather than as a
        /// lid. Everything else about the roof can stay square and it still reads correctly.
        ///
        /// Built from a short chain of tiles stepping out along the diagonal, each one rising
        /// faster than the last. A quadratic rise is what makes a sweep - a linear one is a ramp,
        /// and a ramp on the corner of a roof looks like damage.
        ///
        /// Decoration only. Every piece here is renderer-only, so the roof a player lands on is
        /// still the flat slab the collider describes and parkour is unchanged - a corner tile you
        /// could stand on would be a two-inch ledge at the exact spot people jump for.
        /// </summary>
        private void BuildCurvedEave(Transform parent, float span, float y, string tag)
        {
            float half = span * 0.5f;

            const int steps = 5;
            float reach = Mathf.Min(1.9f, span * 0.11f);
            float lift = reach * 0.85f;

            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps;

                    // Out along the diagonal at a steady rate, up at an accelerating one.
                    float out_ = reach * t;
                    float up = lift * t * t;

                    // Tapering as it goes, so the tip is a point rather than a stub.
                    float width = Mathf.Lerp(1.5f, 0.42f, t);

                    Detail(parent, $"Sweep_{tag}_{sx}_{sz}_{i}",
                        new Vector3((half + out_ * 0.72f) * sx, y + up, (half + out_ * 0.72f) * sz),
                        new Vector3(width, 0.26f, width),
                        _tile);
                }

                // The tip ornament: a small block turned up at the very end of the sweep, which is
                // where a real roof carries a decorated tile.
                Detail(parent, $"SweepTip_{tag}_{sx}_{sz}",
                    new Vector3((half + reach * 0.78f) * sx, y + lift + 0.16f,
                        (half + reach * 0.78f) * sz),
                    new Vector3(0.36f, 0.4f, 0.36f),
                    _darkTimber);
            }

            // Rafter ends along each side, under the eave. Close-packed and small: from the street
            // they are a band of texture rather than individual pieces, and it is the band that
            // says "timber roof" instead of "concrete lid".
            int rafters = Mathf.Clamp(Mathf.RoundToInt(span / 1.1f), 4, 22);

            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;

                for (int i = 0; i < rafters; i++)
                {
                    float t = (i + 0.5f) / rafters;
                    float along = Mathf.Lerp(-half * 0.88f, half * 0.88f, t);

                    // Following the dip in the eave line: lowest in the middle of a side, rising
                    // toward the corners where the sweep takes over.
                    float dip = Mathf.Abs(along) / Mathf.Max(0.01f, half);
                    float rise = lift * dip * dip * 0.5f;

                    Detail(parent, $"Rafter_{tag}_{side}_{i}",
                        horizontal
                            ? new Vector3(along, y - 0.18f + rise, half * sign * 1.02f)
                            : new Vector3(half * sign * 1.02f, y - 0.18f + rise, along),
                        horizontal
                            ? new Vector3(0.34f, 0.2f, 0.5f)
                            : new Vector3(0.5f, 0.2f, 0.34f),
                        _darkTimber);
                }
            }
        }

        /// <summary>
        /// The ridge along the top of a roof and the ornaments at either end.
        ///
        /// Short and entirely cosmetic, but a roof that simply stops at its top slab has no
        /// silhouette against the sky, and the sky is what every rooftop in this town is seen
        /// against.
        /// </summary>
        private void BuildRidge(Transform parent, float span, float y, string tag)
        {
            float length = Mathf.Max(1.5f, span * 0.62f);

            Detail(parent, $"Ridge_{tag}", new Vector3(0f, y + 0.22f, 0f),
                new Vector3(length, 0.34f, 0.55f), _darkTimber);

            // Onigawara: the raised end tiles. Turned outward and up, which is the shape everyone
            // recognises even at a distance where the detail is three pixels.
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Detail(parent, $"RidgeEnd_{tag}_{sx}",
                    new Vector3(length * 0.5f * sx, y + 0.46f, 0f),
                    new Vector3(0.42f, 0.62f, 0.7f), _darkTimber);

                Detail(parent, $"RidgeHorn_{tag}_{sx}",
                    new Vector3(length * 0.5f * sx + 0.18f * sx, y + 0.82f, 0f),
                    new Vector3(0.22f, 0.34f, 0.3f), _darkTimber);
            }
        }

        // ---------------------------------------------------------------- organic shapes

        /// <summary>
        /// A renderer-only child carrying an ORGANIC mesh rather than a box.
        ///
        /// Same contract as Detail: no collider, no acoustics, nothing the simulation can see. The
        /// difference is the silhouette. A tree built from stretched cubes is a stack of stretched
        /// cubes however good the bark on it is, because what identifies a tree at forty metres is
        /// its outline, and that was the whole of why this town read as Minecraft.
        /// </summary>
        /// <summary>Overload taking the mesh last, for calls where the placement is the point.</summary>
        private Transform Organic(Transform parent, Vector3 position, Vector3 scale,
            Material material, Mesh mesh)
            => Organic(parent, "Mass", mesh, position, scale, material);

        private Transform Organic(Transform parent, string name, Mesh mesh, Vector3 position,
            Vector3 scale, Material material)
        {
            var host = new GameObject(name);
            host.transform.SetParent(parent, false);
            host.transform.localPosition = position;
            host.transform.localScale = scale;

            host.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = host.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;

            return host.transform;
        }

        // ---------------------------------------------------------------- mist

        /// <summary>
        /// Mist lying in the streets.
        ///
        /// Global fog gives distance haze but nothing in the near field: a street twenty metres long
        /// is as crisp as a lit room. This scatters flat panels a metre or two off the ground so
        /// there is something between you and the far end of an alley, which is both atmosphere and
        /// stealth - a body at forty metres is a suggestion rather than a target.
        ///
        /// Thickest along the river and in the open plazas, because that is where mist collects and
        /// because those are the places with the longest sightlines to soften.
        ///
        /// No colliders, no lights, no particles. Panels are cheap and the movement is in the
        /// shader.
        /// </summary>
        private void BuildTownMist(float extent, float pitch)
        {
            if (_groundMist == null) return;

            var host = new GameObject("TownMist").transform;
            host.SetParent(_root, false);

            int patches = 0;

            for (int gx = 0; gx <= GridSize; gx++)
            for (int gz = 0; gz <= GridSize; gz++)
            {
                float baseX = (gx - GridSize * 0.5f) * pitch;
                float baseZ = (gz - GridSize * 0.5f) * pitch;

                if (Mathf.Abs(baseX) > extent || Mathf.Abs(baseZ) > extent) continue;

                // Denser near the water. A river valley holds mist; a dry crossing holds less.
                bool nearRiver = _riverColumn >= 0 &&
                                 Mathf.Abs(baseX - _riverCentreX) < RiverWidth * 3f;

                // Thinned right back from the first pass. Panels of forty-odd metres on a
                // forty-six metre street grid overlap several deep, and alpha compounds: five
                // layers at 0.3 each is 83% opaque, which is why the first attempt read as spilled
                // white paint on the river rather than as mist.
                // Turned back up. This was thinned hard when overlapping panels were compounding
                // into white paint, but the fix for that was the density and the falloff, not the
                // count - and at a quarter of the crossings the town had haze in the distance and
                // nothing at street level, which is where atmosphere is actually felt.
                double chance = nearRiver ? 0.9 : 0.55;
                if (_random.NextDouble() > chance) continue;

                int here = nearRiver ? 2 : 1;

                for (int i = 0; i < here; i++)
                {
                    var at = new Vector3(
                        baseX + (float)(_random.NextDouble() * 2f - 1f) * pitch * 0.5f,
                        0f,
                        baseZ + (float)(_random.NextDouble() * 2f - 1f) * pitch * 0.5f);

                    // Sit it just above whatever is underneath, so mist in a street lies on the
                    // street and mist over the river lies on the water.
                    float y = 1.1f + (float)_random.NextDouble() * 1.6f;
                    bool overChannel = _riverColumn >= 0 &&
                                       Mathf.Abs(at.x - _riverCentreX) < RiverWidth * 0.5f;

                    if (overChannel)
                    {
                        // The water surface has no collider - that is the whole point of it - so a
                        // downward ray goes straight through to the riverbed and the panel ends up
                        // at or below the waterline, half-swallowed by an opaque surface. Over the
                        // channel the height comes from the water itself.
                        y = (-RiverDepth + WaterDepth) + 0.8f + (float)_random.NextDouble() * 1.4f;
                    }
                    else if (Physics.Raycast(at + Vector3.up * 60f, Vector3.down, out RaycastHit ground,
                                 140f, UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore))
                    {
                        // Only ground-level surfaces. Mist on a rooftop is a different weather.
                        if (ground.point.y > 3f) continue;
                        y += ground.point.y;
                    }

                    float size = 34f + (float)_random.NextDouble() * 40f;

                    var panel = new GameObject($"Mist_{gx}_{gz}_{i}");
                    panel.transform.SetParent(host, false);
                    panel.transform.localPosition = new Vector3(at.x, y, at.z);

                    // Laid flat, with a little tilt so the layer is not a single plane the eye can
                    // find the height of.
                    panel.transform.localRotation = Quaternion.Euler(
                        90f + (float)(_random.NextDouble() * 2f - 1f) * 6f,
                        (float)_random.NextDouble() * 360f,
                        0f);

                    panel.transform.localScale = new Vector3(size, size, 1f);

                    panel.AddComponent<MeshFilter>().sharedMesh = MistQuad();
                    var renderer = panel.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = _groundMist;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;

                    patches++;
                }
            }

            Debug.Log($"[Unseen] town mist: {patches} panels lying in the streets");
        }

        private static Mesh _mistQuad;

        /// <summary>A unit quad with UVs, shared by every mist panel.</summary>
        private static Mesh MistQuad()
        {
            if (_mistQuad != null) return _mistQuad;

            _mistQuad = new Mesh { name = "MistQuad" };
            _mistQuad.SetVertices(new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f)
            });
            _mistQuad.SetUVs(0, new System.Collections.Generic.List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            });
            _mistQuad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _mistQuad.RecalculateNormals();
            _mistQuad.RecalculateBounds();
            return _mistQuad;
        }

        // ---------------------------------------------------------------- greenery and wildlife

        /// <summary>
        /// Clipped hedges, potted plants and climbing growth along the streets.
        ///
        /// Separate from the groves because it serves a different purpose: the trees break
        /// sightlines down a long street, and this fills the ankle-to-waist band that was bare
        /// gravel everywhere. It is also where most of the birds and animals live, which is the
        /// point of putting it near where people walk rather than out in the gardens.
        /// </summary>
        private void BuildHedges(float extent, float pitch)
        {
            var green = new GameObject("Greenery").transform;
            green.SetParent(_root, false);

            int hedges = 0;
            int pots = 0;

            for (int gx = 0; gx <= GridSize; gx++)
            for (int gz = 0; gz <= GridSize; gz++)
            {
                float baseX = (gx - GridSize * 0.5f) * pitch;
                float baseZ = (gz - GridSize * 0.5f) * pitch;

                if (Mathf.Abs(baseX) > extent || Mathf.Abs(baseZ) > extent) continue;
                if (_riverColumn >= 0 && Mathf.Abs(baseX - _riverCentreX) < RiverWidth) continue;

                // A run of clipped hedge along one side of the crossing. Occluders, because a hedge
                // at chest height genuinely hides a crouching body - which is the whole reason to
                // put one on a street in a game about not being seen.
                if (_random.NextDouble() < 0.4)
                {
                    bool alongX = _random.NextDouble() < 0.5;
                    float length = pitch * (0.28f + (float)_random.NextDouble() * 0.3f);
                    float height = 0.9f + (float)_random.NextDouble() * 0.5f;
                    float offset = 7.5f + (float)_random.NextDouble() * 2.5f;
                    float side = _random.NextDouble() < 0.5 ? 1f : -1f;

                    var at = alongX
                        ? new Vector3(baseX, height * 0.5f, baseZ + side * offset)
                        : new Vector3(baseX + side * offset, height * 0.5f, baseZ);

                    var size = alongX
                        ? new Vector3(length, height, 0.9f)
                        : new Vector3(0.9f, height, length);

                    Transform hedge = BuildHedge(green, $"Hedge_{gx}_{gz}", at, size, alongX);
                    Acoustics(hedge, 0.3f, 0.8f, 0.9f);
                    hedges++;

                    // Birds sit on hedges. Perched at the top, which is where a startled one is
                    // most visible going up.
                    if (_random.NextDouble() < 0.45)
                        BuildBird(green, at + new Vector3(0f, height * 0.5f + 0.2f, 0f));
                }

                // Potted plants against a wall, the way a shop front is dressed.
                if (_random.NextDouble() < 0.45)
                {
                    int count = 1 + _random.Next(3);
                    float side = _random.NextDouble() < 0.5 ? 1f : -1f;

                    for (int i = 0; i < count; i++)
                    {
                        var at = new Vector3(
                            baseX + side * (6.4f + i * 0.85f),
                            0f,
                            baseZ + (float)(_random.NextDouble() * 2f - 1f) * 4f);

                        Detail(green, $"Pot_{gx}_{gz}_{i}",
                            at + new Vector3(0f, 0.22f, 0f),
                            new Vector3(0.5f, 0.44f, 0.5f), _stone);

                        Detail(green, $"Potted_{gx}_{gz}_{i}",
                            at + new Vector3(0f, 0.72f, 0f),
                            new Vector3(0.62f, 0.6f, 0.62f), _foliage);
                        pots++;
                    }
                }

                // Something small living in the gap between the hedge and the wall.
                if (_random.NextDouble() < 0.3)
                    BuildAnimal(green, new Vector3(
                        baseX + (float)(_random.NextDouble() * 2f - 1f) * 6f, 0f,
                        baseZ + (float)(_random.NextDouble() * 2f - 1f) * 6f));
            }

            Debug.Log($"[Unseen] greenery: {hedges} hedges, {pots} potted plants");
        }

        /// <summary>
        /// A clipped hedge: a run of overlapping foliage masses over an invisible box.
        ///
        /// It used to BE the box - one cube with a leaf texture on it, which is exactly the thing
        /// that reads as Minecraft. A hedge is not a cuboid; it is a row of shrubs grown into each
        /// other, and the only part of it the eye actually reads is the ragged top line.
        ///
        /// The box is still there and still does all the work that matters. Line of sight, the
        /// footstep acoustics and the crouch-behind-it cover all come off the collider, so the
        /// shape a player hides behind is unchanged and is still a stable, predictable rectangle -
        /// which is what cover in a stealth game has to be. Only the renderer is thrown away, and
        /// the masses that replace it are deliberately grown a little wider than the collider so
        /// nobody's shoulder pokes visibly through the leaves.
        /// </summary>
        private Transform BuildHedge(Transform parent, string name, Vector3 at, Vector3 size,
            bool alongX)
        {
            Transform hedge = Box(parent, name, at, size, UnseenLayers.Occluder, _foliage);

            var renderer = hedge.GetComponent<MeshRenderer>();
            if (renderer != null) UnseenObject.Destroy(renderer);

            float length = alongX ? size.x : size.z;
            float depth = alongX ? size.z : size.x;
            float height = size.y;

            // Spaced at about two thirds of a mass, so each one buries a third of itself in its
            // neighbour and the run has no seams.
            float mass = height * 1.05f;
            int count = Mathf.Max(2, Mathf.RoundToInt(length / (mass * 0.62f)));

            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0.5f : i / (float)(count - 1);
                float along = Mathf.Lerp(-length * 0.5f, length * 0.5f, t);

                // Deterministic per-position variation. A run of identical spheres is a caterpillar.
                float wobble = Mathf.Sin((at.x + at.z) * 0.7f + i * 2.3f);
                float rise = 1f + wobble * 0.16f;
                float lean = wobble * depth * 0.22f;

                Vector3 offset = alongX
                    ? new Vector3(along, 0f, lean)
                    : new Vector3(lean, 0f, along);

                // Sat slightly low so the masses read as growing out of the ground rather than
                // resting on it, and clipped just proud of the collider on every axis.
                offset.y = height * (0.06f + 0.04f * wobble);

                Organic(hedge, $"Mass_{i}",
                    OrganicMeshFactory.Blob(5, 9, 0.44f, i % 8),
                    offset,
                    new Vector3(mass * 0.86f, height * rise, depth * 1.5f),
                    _foliage);
            }

            // A few sprigs standing proud of the top line. The silhouette is the only part of a
            // hedge anybody looks at, and a smooth one still reads as topiary.
            int sprigs = 2 + _random.Next(3);
            for (int i = 0; i < sprigs; i++)
            {
                float t = (i + 0.5f) / sprigs;
                float along = Mathf.Lerp(-length * 0.42f, length * 0.42f, t);

                Vector3 offset = alongX
                    ? new Vector3(along, 0f, (float)(_random.NextDouble() - 0.5) * depth * 0.5f)
                    : new Vector3((float)(_random.NextDouble() - 0.5) * depth * 0.5f, 0f, along);

                offset.y = height * 0.46f;

                Organic(hedge, $"Sprig_{i}",
                    OrganicMeshFactory.Blade(3, 0.35f),
                    offset,
                    new Vector3(0.26f, 0.3f + (float)_random.NextDouble() * 0.22f, 0.26f),
                    _reed).localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);
            }

            return hedge;
        }

        /// <summary>
        /// A bird: body, head, tail and a wing either side.
        ///
        /// Small and dark on purpose. It is meant to be almost invisible until it moves, so that
        /// what a player notices is the departure rather than the bird.
        /// </summary>
        private void BuildBird(Transform parent, Vector3 at)
        {
            if (!CrittersEnabled()) return;

            var bird = new GameObject($"Bird_{_birds}").transform;
            bird.SetParent(parent, false);
            bird.localPosition = at;
            bird.localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);
            _birds++;

            Detail(bird, "Body", new Vector3(0f, 0f, 0f),
                new Vector3(0.17f, 0.15f, 0.28f), _darkTimber);
            Detail(bird, "Head", new Vector3(0f, 0.09f, 0.15f),
                new Vector3(0.12f, 0.11f, 0.12f), _darkTimber);
            Detail(bird, "Tail", new Vector3(0f, 0.02f, -0.22f),
                new Vector3(0.1f, 0.04f, 0.18f), _darkTimber);

            Transform left = Detail(bird, "WingL", new Vector3(-0.11f, 0.03f, 0f),
                new Vector3(0.2f, 0.04f, 0.24f), _darkTimber);
            Transform right = Detail(bird, "WingR", new Vector3(0.11f, 0.03f, 0f),
                new Vector3(0.2f, 0.04f, 0.24f), _darkTimber);

            var critter = bird.gameObject.AddComponent<Critter>();
            critter.StartleRadius = 9f;
            critter.Configure(Critter.Species.Bird, left, right);
        }

        /// <summary>A cat or a fox: low body, four short legs, a tail that gives it away.</summary>
        private void BuildAnimal(Transform parent, Vector3 at)
        {
            if (!CrittersEnabled()) return;

            var animal = new GameObject($"Animal_{_animals}").transform;
            animal.SetParent(parent, false);
            animal.localPosition = at;
            animal.localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);
            _animals++;

            Detail(animal, "Body", new Vector3(0f, 0.22f, 0f),
                new Vector3(0.2f, 0.18f, 0.46f), _darkTimber);
            Detail(animal, "Rump", new Vector3(0f, 0.24f, -0.19f),
                new Vector3(0.22f, 0.2f, 0.16f), _darkTimber);
            Detail(animal, "Head", new Vector3(0f, 0.3f, 0.29f),
                new Vector3(0.16f, 0.15f, 0.16f), _darkTimber);

            // A snout and two ears. Three small boxes, and the difference between a cat and a loaf.
            Detail(animal, "Snout", new Vector3(0f, 0.27f, 0.39f),
                new Vector3(0.09f, 0.08f, 0.09f), _darkTimber);

            for (int e = -1; e <= 1; e += 2)
            {
                Transform ear = Detail(animal, $"Ear_{e}", new Vector3(e * 0.055f, 0.39f, 0.27f),
                    new Vector3(0.05f, 0.09f, 0.03f), _darkTimber);
                ear.localRotation = Quaternion.Euler(-12f, 0f, e * 16f);
            }

            // The tail in two segments, tapering and lifted, rather than one straight peg.
            Transform tailBase = Detail(animal, "Tail", new Vector3(0f, 0.28f, -0.34f),
                new Vector3(0.07f, 0.07f, 0.2f), _darkTimber);
            tailBase.localRotation = Quaternion.Euler(-18f, 0f, 0f);

            Transform tailTip = Detail(animal, "TailTip", new Vector3(0f, 0.36f, -0.48f),
                new Vector3(0.05f, 0.05f, 0.16f), _darkTimber);
            tailTip.localRotation = Quaternion.Euler(-42f, 0f, 0f);

            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * 0.07f;
                float z = (i < 2 ? 1f : -1f) * 0.15f;
                Detail(animal, $"Leg_{i}", new Vector3(x, 0.07f, z),
                    new Vector3(0.06f, 0.15f, 0.06f), _darkTimber);
            }

            // Smaller radius than a bird: something on the ground lets you get closer before it
            // decides, and it is much quieter when it goes.
            var critter = animal.gameObject.AddComponent<Critter>();
            critter.StartleRadius = 6f;
            critter.Configure(Critter.Species.Animal, null, null);
        }

        private static bool CrittersEnabled()
        {
            UnseenConfig config = UnseenConfig.Default;
            return config == null || config.Critters.Enabled;
        }

        // ---------------------------------------------------------------- verges

        /// <summary>
        /// Patches of worn grass and bare earth across the streets.
        ///
        /// A town where every square metre of ground is the same gravel reads as a floor with
        /// buildings placed on it. Real streets wear unevenly: grass survives where nobody walks,
        /// and the paving gives up entirely where everybody does. These are flat, thin and
        /// collider-free - they change what the ground looks like and nothing else.
        /// </summary>
        private void BuildVerges(float extent, float pitch)
        {
            var verges = new GameObject("Verges").transform;
            verges.SetParent(_root, false);

            int patches = 0;

            for (int gx = 0; gx <= GridSize; gx++)
            for (int gz = 0; gz <= GridSize; gz++)
            {
                float baseX = (gx - GridSize * 0.5f) * pitch;
                float baseZ = (gz - GridSize * 0.5f) * pitch;

                for (int i = 0; i < 3; i++)
                {
                    if (_random.NextDouble() > 0.45) continue;

                    var at = new Vector3(
                        baseX + (float)(_random.NextDouble() * 2f - 1f) * pitch * 0.42f,
                        0.03f,
                        baseZ + (float)(_random.NextDouble() * 2f - 1f) * pitch * 0.42f);

                    if (Mathf.Abs(at.x) > extent || Mathf.Abs(at.z) > extent) continue;
                    if (_riverColumn >= 0 && Mathf.Abs(at.x - _riverCentreX) < RiverWidth * 0.8f) continue;

                    bool grass = _random.NextDouble() < 0.55;
                    float w = 2.5f + (float)_random.NextDouble() * 5f;
                    float d = 2.5f + (float)_random.NextDouble() * 5f;

                    Transform patch = Detail(verges, $"{(grass ? "Grass" : "Dirt")}_{gx}_{gz}_{i}",
                        at, new Vector3(w, 0.06f, d), grass ? _grass : _dirt);
                    patch.localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 90f, 0f);
                    patches++;

                    // Moss on the damp side of some patches, which is what ground looks like where
                    // a wall keeps the sun off it.
                    if (grass && _random.NextDouble() < 0.35)
                        Detail(verges, $"Moss_{gx}_{gz}_{i}",
                            at + new Vector3((float)(_random.NextDouble() * 2f - 1f) * w * 0.3f,
                                0.015f,
                                (float)(_random.NextDouble() * 2f - 1f) * d * 0.3f),
                            new Vector3(w * 0.55f, 0.05f, d * 0.55f), _moss);

                    // A few blades standing up out of the patch, so it is not a flat decal. Thin
                    // and leaning, for the same reason the reeds are: a cube of foliage on the
                    // ground looks like litter, not like grass.
                    if (!grass) continue;
                    int tufts = 3 + _random.Next(4);
                    for (int t = 0; t < tufts; t++)
                    {
                        float tall = 0.3f + (float)_random.NextDouble() * 0.35f;
                        var tuftAt = at + new Vector3(
                            (float)(_random.NextDouble() * 2f - 1f) * w * 0.38f, tall * 0.45f,
                            (float)(_random.NextDouble() * 2f - 1f) * d * 0.38f);

                        Transform blade = Organic(verges, $"Tuft_{gx}_{gz}_{i}_{t}",
                            OrganicMeshFactory.Blade(3, 0.42f),
                            tuftAt - new Vector3(0f, tall * 0.45f, 0f),
                            new Vector3(0.14f, tall, 1f), _reed);
                        blade.localRotation = Quaternion.Euler(
                            (float)(_random.NextDouble() * 2f - 1f) * 24f,
                            (float)_random.NextDouble() * 90f,
                            (float)(_random.NextDouble() * 2f - 1f) * 24f);
                    }
                }
            }

            Debug.Log($"[Unseen] verges: {patches} patches of grass and bare earth");
        }

        // ---------------------------------------------------------------- streets        // ---------------------------------------------------------------- streets

        /// <summary>
        /// What a street has on it besides buildings: a drainage channel down each side, wells at
        /// some crossings, notice boards, and the barrels and crates that pile up outside a shop.
        ///
        /// The ground was one flat slab from wall to wall, which is what made the town read as
        /// buildings standing on a floor rather than a place with streets in it. None of this is
        /// tall enough to fight the camera or to hide behind - it is there to give the eye
        /// something at ankle height and to break the run of open gravel.
        /// </summary>
        private void BuildStreetFurniture(float extent, float pitch)
        {
            var street = new GameObject("StreetFurniture").transform;
            street.SetParent(_root, false);

            int wells = 0;
            int boards = 0;
            int stacks = 0;

            for (int gx = 0; gx <= GridSize; gx++)
            for (int gz = 0; gz <= GridSize; gz++)
            {
                float x = (gx - GridSize * 0.5f) * pitch;
                float z = (gz - GridSize * 0.5f) * pitch;

                if (Mathf.Abs(x) > extent || Mathf.Abs(z) > extent) continue;
                if (_riverColumn >= 0 && Mathf.Abs(x - _riverCentreX) < RiverWidth) continue;

                // A gutter running along each street line. Shallow, stone-lined, and the single
                // cheapest thing that makes a street look like a street.
                if (gz < GridSize)
                {
                    Detail(street, $"Gutter_X_{gx}_{gz}",
                        new Vector3(x - 4.6f, 0.04f, z + pitch * 0.5f),
                        new Vector3(0.7f, 0.1f, pitch * 0.86f), _stone);
                    Detail(street, $"Gutter_X2_{gx}_{gz}",
                        new Vector3(x + 4.6f, 0.04f, z + pitch * 0.5f),
                        new Vector3(0.7f, 0.1f, pitch * 0.86f), _stone);
                }

                if (_random.NextDouble() < 0.14)
                {
                    BuildWell(street, new Vector3(x + 3.5f, 0f, z + 3.5f), wells++);
                    continue;
                }

                if (_random.NextDouble() < 0.12)
                {
                    BuildNoticeBoard(street, new Vector3(x - 3.2f, 0f, z + 2.4f), boards++);
                    continue;
                }

                if (_random.NextDouble() < 0.3)
                {
                    BuildGoodsStack(street, new Vector3(x + 2.6f, 0f, z - 3.4f), stacks++);
                }
            }

            Debug.Log($"[Unseen] street furniture: {wells} wells, {boards} notice boards, " +
                      $"{stacks} stacks of goods");
        }

        /// <summary>A stone well head with a timber frame and a bucket beam over it.</summary>
        private void BuildWell(Transform street, Vector3 at, int index)
        {
            var well = new GameObject($"Well_{index}").transform;
            well.SetParent(street, false);
            well.localPosition = at;

            Transform kerb = Box(well, "Kerb", new Vector3(0f, 0.35f, 0f),
                new Vector3(2.1f, 0.7f, 2.1f), UnseenLayers.Occluder, _stone);
            Acoustics(kerb, 0.9f, 1f, 1f);

            // The shaft, dark and inset, so the well is a hole rather than a plinth.
            Detail(well, "Shaft", new Vector3(0f, 0.66f, 0f),
                new Vector3(1.5f, 0.1f, 1.5f), _darkTimber);

            for (int i = -1; i <= 1; i += 2)
                Detail(well, $"Post_{i}", new Vector3(i * 0.85f, 1.6f, 0f),
                    new Vector3(0.18f, 2.6f, 0.18f), _darkTimber);

            Detail(well, "Beam", new Vector3(0f, 2.85f, 0f),
                new Vector3(2.2f, 0.2f, 0.2f), _darkTimber);

            Detail(well, "Bucket", new Vector3(0f, 2.35f, 0f),
                new Vector3(0.42f, 0.4f, 0.42f), _woodFloor);

            Detail(well, "Rope", new Vector3(0f, 2.62f, 0f),
                new Vector3(0.05f, 0.42f, 0.05f), _rafter);
        }

        /// <summary>A kosatsu: the roofed board a domain posted its edicts on.</summary>
        private void BuildNoticeBoard(Transform street, Vector3 at, int index)
        {
            var board = new GameObject($"Notice_{index}").transform;
            board.SetParent(street, false);
            board.localPosition = at;
            board.localRotation = Quaternion.Euler(0f, (index * 47f) % 360f, 0f);

            for (int i = -1; i <= 1; i += 2)
            {
                Transform post = Box(board, $"Post_{i}", new Vector3(i * 0.8f, 1.15f, 0f),
                    new Vector3(0.16f, 2.3f, 0.16f), UnseenLayers.Occluder, _darkTimber);
                Acoustics(post, 0.5f, 1f, 1f);
            }

            Detail(board, "Panel", new Vector3(0f, 1.7f, 0f),
                new Vector3(1.75f, 1.15f, 0.09f), _paper);

            Detail(board, "Frame", new Vector3(0f, 1.08f, 0f),
                new Vector3(1.9f, 0.12f, 0.16f), _darkTimber);

            // A little roof over it, because paper in the rain does not last.
            Detail(board, "Roof", new Vector3(0f, 2.42f, 0f),
                new Vector3(2.2f, 0.14f, 0.8f), _tile);
            Detail(board, "RoofRidge", new Vector3(0f, 2.54f, 0f),
                new Vector3(2.3f, 0.12f, 0.24f), _darkTimber);
        }

        /// <summary>Barrels and crates stacked outside a shop front.</summary>
        private void BuildGoodsStack(Transform street, Vector3 at, int index)
        {
            var stack = new GameObject($"Goods_{index}").transform;
            stack.SetParent(street, false);
            stack.localPosition = at;

            int items = 2 + _random.Next(4);
            for (int i = 0; i < items; i++)
            {
                bool barrel = _random.NextDouble() < 0.5;
                var offset = new Vector3(
                    (float)(_random.NextDouble() * 2f - 1f) * 1.3f, 0f,
                    (float)(_random.NextDouble() * 2f - 1f) * 1.3f);

                float size = barrel ? 0.7f : 0.85f;
                float height = barrel ? 0.95f : 0.7f;

                Transform item = Box(stack, $"{(barrel ? "Barrel" : "Crate")}_{i}",
                    offset + new Vector3(0f, height * 0.5f, 0f),
                    new Vector3(size, height, size), UnseenLayers.Occluder,
                    barrel ? _rafter : _woodFloor);
                item.localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 90f, 0f);
                Acoustics(item, 0.6f, 1f, 1f);

                // Hoops on a barrel, so it is not just a short box.
                if (!barrel) continue;
                for (int h = 0; h < 2; h++)
                    Detail(stack, $"Hoop_{i}_{h}",
                        offset + new Vector3(0f, height * (0.25f + h * 0.5f), 0f),
                        new Vector3(size + 0.06f, 0.09f, size + 0.06f), _darkTimber);
            }
        }

        // ---------------------------------------------------------------- foliage        // ---------------------------------------------------------------- foliage

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

                // Corners are for lamp posts; trees go along the streets between them. Three
                // planting points per street rather than two, and taken more often: a town whose
                // streets are lined with green is a town where a sprint has somewhere to be heard
                // from, now that most trees have a bird in them.
                for (int step = 1; step <= 3; step++)
                {
                    if (_random.NextDouble() > 0.45) continue;

                    bool alongZ = _random.NextDouble() < 0.5;
                    float slide = pitch * (step / 4f);

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

        /// <summary>
        /// A tree. Three species, because a street lined with one shape is a street lined with one
        /// asset.
        ///
        /// The trunk is an occluder, so a tree genuinely breaks a sightline down a long street.
        /// Everything above it is decoration with no collider: a canopy that blocked sight would
        /// hide rooftop ninjas from the ground for free, and one you could stand on would turn
        /// every avenue into a walkway.
        /// </summary>
        private void BuildTree(Transform grove, Vector3 position)
        {
            var tree = new GameObject("Tree").transform;
            tree.SetParent(grove, false);
            tree.localPosition = position;
            tree.localRotation = Quaternion.Euler(0f, (float)_random.NextDouble() * 360f, 0f);

            int species = _random.Next(3);
            float height = 4.5f + (float)_random.NextDouble() * 3.5f;
            float spread = 2.2f + (float)_random.NextDouble() * 1.6f;
            float lean = (float)(_random.NextDouble() * 2f - 1f) * 4f;

            // The trunk is a tapered, bent tube. The COLLIDER stays a box - it is what breaks a
            // sightline down a street and a six-sided mesh collider on two hundred trees is not a
            // trade worth making for a shape nobody bumps into precisely.
            Transform trunk = Box(tree, "TrunkCollider", new Vector3(0f, height * 0.5f, 0f),
                new Vector3(0.42f, height, 0.42f), UnseenLayers.Occluder, null);
            trunk.localRotation = Quaternion.Euler(lean, 0f, lean * 0.6f);
            Acoustics(trunk, 0.6f, 1f, 1f);

            MeshRenderer boxRenderer = trunk.GetComponent<MeshRenderer>();
            if (boxRenderer != null) boxRenderer.enabled = false;

            float bend = (float)(_random.NextDouble() * 2f - 1f) * 0.12f;
            Transform bole = Organic(tree, "Trunk",
                OrganicMeshFactory.Tube(6, 5, 0.55f, bend, 0.35f),
                Vector3.zero, new Vector3(0.62f, height, 0.62f), _darkTimber);
            bole.localRotation = Quaternion.Euler(lean * 0.5f, (float)_random.NextDouble() * 360f,
                lean * 0.4f);

            // Roots flaring at the base, so the trunk meets the ground instead of being planted
            // in it like a post.
            // Roots flaring at the base: short tubes laid nearly flat, splaying outward.
            for (int r = 0; r < 5; r++)
            {
                float angle = r * 72f + 25f;
                float rad = angle * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                Transform root = Organic(tree, $"Root_{r}",
                    OrganicMeshFactory.Tube(5, 3, 0.3f, 0.25f, 0.4f),
                    dir * 0.2f + new Vector3(0f, 0.06f, 0f),
                    new Vector3(0.34f, 0.85f, 0.34f), _darkTimber);

                // Laid over so it runs along the ground away from the bole.
                root.localRotation = Quaternion.Euler(72f, -angle, 0f);
            }

            switch (species)
            {
                case 0:
                    BuildPine(tree, height, spread);
                    break;
                case 1:
                    BuildBroadleaf(tree, height, spread);
                    break;
                default:
                    BuildBambooClump(tree, height, spread);
                    break;
            }

            // Most trees have something in them. A tree-lined street is now a street you cannot
            // sprint down without announcing it.
            if (_random.NextDouble() < 0.7)
                BuildBird(tree, new Vector3(
                    (float)(_random.NextDouble() * 2f - 1f) * spread * 0.4f,
                    height * (0.62f + (float)_random.NextDouble() * 0.25f),
                    (float)(_random.NextDouble() * 2f - 1f) * spread * 0.4f));
        }

        /// <summary>A pine: bare trunk, branches near the top, tiered plates of needles.</summary>
        private void BuildPine(Transform tree, float height, float spread)
        {
            for (int b = 0; b < 3; b++)
            {
                float angle = b * 120f + 20f;
                float y = height * (0.55f + b * 0.13f);
                var dir = new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0f, Mathf.Cos(angle * Mathf.Deg2Rad));

                Transform branch = Organic(tree, $"Branch_{b}",
                    OrganicMeshFactory.Tube(4, 3, 0.35f, 0.3f, 0.3f),
                    dir * spread * 0.18f + new Vector3(0f, y, 0f),
                    new Vector3(0.16f, spread * 0.75f, 0.16f), _darkTimber);
                branch.localRotation = Quaternion.Euler(74f, -angle, 0f);
            }

            // Tiers of needles as flattened lumpy masses rather than plates. A pine tier is a
            // ragged disc of foliage; a box reads as a shelf.
            for (int tier = 0; tier < 5; tier++)
            {
                float t = tier / 4f;
                float y = Mathf.Lerp(height * 0.5f, height + 0.9f, t);
                float size = Mathf.Lerp(spread * 1.1f, spread * 0.22f, t);

                Transform plate = Organic(tree, $"Canopy_{tier}",
                    OrganicMeshFactory.Blob(6, 10, 0.3f, tier),
                    new Vector3(0f, y, 0f),
                    new Vector3(size, size * 0.42f, size), _foliage);
                plate.localRotation = Quaternion.Euler(0f, tier * 37f, 0f);
            }
        }

        /// <summary>A broadleaf: a rounded crown built from overlapping clumps.</summary>
        private void BuildBroadleaf(Transform tree, float height, float spread)
        {
            for (int b = 0; b < 3; b++)
            {
                float angle = b * 120f;
                var dir = new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0f, Mathf.Cos(angle * Mathf.Deg2Rad));

                Transform limb = Organic(tree, $"Limb_{b}",
                    OrganicMeshFactory.Tube(5, 4, 0.3f, 0.35f, 0.3f),
                    dir * spread * 0.16f + new Vector3(0f, height * 0.5f, 0f),
                    new Vector3(0.26f, height * 0.55f, 0.26f), _darkTimber);
                limb.localRotation = Quaternion.Euler(26f, -angle, 0f);
            }

            for (int c = 0; c < 5; c++)
            {
                float angle = c * 72f + 15f;
                var dir = new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0f, Mathf.Cos(angle * Mathf.Deg2Rad));
                float y = height * (0.86f + (c % 2) * 0.14f);
                float size = spread * (0.7f + (c % 3) * 0.14f);

                Transform clump = Organic(tree, $"Crown_{c}",
                    OrganicMeshFactory.Blob(7, 12, 0.34f, c),
                    dir * spread * 0.34f + new Vector3(0f, y, 0f),
                    new Vector3(size, size * 0.8f, size), _foliage);
                clump.localRotation = Quaternion.Euler(0f, angle, 0f);
            }
        }

        /// <summary>A clump of garden bamboo: several thin canes with leaf heads.</summary>
        private void BuildBambooClump(Transform tree, float height, float spread)
        {
            int canes = 5 + _random.Next(4);

            for (int c = 0; c < canes; c++)
            {
                float angle = c * (360f / canes) + (float)_random.NextDouble() * 20f;
                var dir = new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0f, Mathf.Cos(angle * Mathf.Deg2Rad));
                float caneHeight = height * (0.85f + (float)_random.NextDouble() * 0.5f);
                Vector3 at = dir * spread * 0.3f * (float)_random.NextDouble();

                Transform cane = Organic(tree, $"Cane_{c}",
                    OrganicMeshFactory.Tube(5, 4, 0.75f, 0.1f, 0.1f),
                    at, new Vector3(0.16f, caneHeight, 0.16f), _bamboo);
                cane.localRotation = Quaternion.Euler(dir.z * 7f, 0f, -dir.x * 7f);

                Organic(tree, $"CaneLeaves_{c}",
                    OrganicMeshFactory.Blob(5, 9, 0.4f, c),
                    at + new Vector3(0f, caneHeight * 0.9f, 0f),
                    new Vector3(1.2f, 0.85f, 1.2f), _foliage);
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

            // The bush is a lumpy mass; the collider stays a box. A shrub is cover you crouch
            // behind and a box is a perfectly good description of that, whereas a box is not a
            // remotely good description of what a shrub looks like.
            var size = new Vector3(width, height, width);

            host.AddComponent<MeshFilter>().sharedMesh =
                OrganicMeshFactory.Blob(6, 10, 0.32f, _random.Next(8));
            host.transform.localScale = size;

            host.AddComponent<MeshRenderer>().sharedMaterial = _foliage;
            host.AddComponent<BoxCollider>().size = Vector3.one;
            host.isStatic = true;
            Acoustics(host.transform, 0.25f, 0.7f, 0.8f);

            // Two smaller masses leaning out of it, so the outline is not one dome.
            for (int i = 0; i < 2; i++)
            {
                float angle = (float)_random.NextDouble() * Mathf.PI * 2f;
                var off = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * width * 0.3f;

                Organic(shrub, $"Bush_{i}", OrganicMeshFactory.Blob(5, 8, 0.36f, _random.Next(8)),
                    off + new Vector3(0f, height * (0.35f + (float)_random.NextDouble() * 0.3f), 0f),
                    new Vector3(width * 0.62f, height * 0.6f, width * 0.62f), _foliage);
            }
        }

        /// <summary>
        /// Plants the spirit forest against the inside of the rampart, dormant until the match
        /// tells it to grow.
        ///
        /// Built here rather than by the growth system so the geometry exists before anyone
        /// connects: a wall of bamboo appearing as a few hundred new GameObjects three minutes into
        /// a match is a hitch nobody needs, and the whole thing costs nothing while it is switched
        /// off.
        /// </summary>
        private void BuildSpiritForest()
        {
            if (_rampartRing <= 0f) return;

            var host = new GameObject("SpiritForest").transform;
            host.SetParent(_root, false);

            var forest = host.gameObject.AddComponent<BambooForest>();
            _forest = forest;

            // The ring starts at the rampart and closes from there, so its maximum radius is the
            // inside of the wall-walk: any further out and the bamboo would be standing behind the
            // wall where nobody can see it.
            const float bankHeight = 5.4f;
            const float parapetHeight = 1.6f;
            const float walkWidth = 4f;

            float wallHeight = bankHeight + parapetHeight;
            float inner = _rampartRing - walkWidth * 0.5f;

            // Twice the rampart was the original spec, and on its own it is not enough: the
            // pagodas stand well above that and a fourteen metre wall can be dropped onto from
            // one. The forest has to top the tallest thing in the town, so it is measured against
            // what was actually built rather than against a number somebody guessed.
            float tallest = 0f;
            var standing = _root.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < standing.Length; i++)
                if (standing[i] != null) tallest = Mathf.Max(tallest, standing[i].bounds.max.y);

            float wanted = wallHeight * Mathf.Max(1f, MaterialSetBambooHeight());
            float clearing = tallest + BambooClearance;

            Debug.Log($"[Unseen] tallest structure {tallest:0.0} m; spirit forest stands " +
                      $"{Mathf.Max(wanted, clearing):0.0} m");

            GreyboxMaterialSet set = MaterialSet != null ? MaterialSet : GreyboxMaterialSet.Load();
            Material tuft = set != null && set.BambooLeaf != null ? set.BambooLeaf : _foliage;

            forest.Build(inner, Mathf.Max(wanted, clearing), _bamboo, _bambooMass, tuft);
        }

        /// <summary>Height multiple, read from config so the forest and the rules agree.</summary>
        private static float MaterialSetBambooHeight()
        {
            UnseenConfig config = UnseenConfig.Default;
            return config != null ? config.Bamboo.HeightMultiple : 2f;
        }

        // ---------------------------------------------------------------- cost control        // ---------------------------------------------------------------- cost control

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
            // Derived from the plot rather than the grid: blocks vary in size, and trim sized to
            // the nominal block would overhang a small one and fall short on a large one.
            float length = half * 2f;

            const float faceOffset = 0.22f; // just proud of the 0.4 m wall, so nothing z-fights
            const int bays = 5;

            float outward = (half + faceOffset) * sign;

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

            // Namako-kabe: the boarded wainscot that protects the foot of a plaster wall from rain
            // splash and cart wheels. Every machiya has one, and it is the detail that stops a
            // white wall meeting the ground in a single flat line.
            Detail(compound, $"Wainscot_{side}", Along(0f, 1.35f), Size(length, 1.3f, 0.2f),
                _darkTimber);

            Detail(compound, $"WainscotCap_{side}", Along(0f, 2.02f), Size(length, 0.12f, 0.26f),
                _tile);

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

            // Mushiko-mado: the barred insect-cage window of an upper storey. Slatted rather than
            // glazed, so it reads as a row of dark bars in a pale wall.
            for (int bay = 0; bay < bays; bay++)
            {
                if (hasDoorway && bay == bays / 2) continue;
                if ((bay + side) % 2 != 0) continue;

                float centre = Mathf.Lerp(-length * 0.5f, length * 0.5f, (bay + 0.5f) / bays);
                float sill = height * 0.72f;

                Detail(compound, $"Window_{side}_{bay}", Along(centre, sill),
                    Size(2.1f, 1.15f, 0.14f), _darkTimber);

                // The bars themselves, so the opening has depth at close range.
                for (int bar = 0; bar < 5; bar++)
                {
                    float t = (bar + 0.5f) / 5f;
                    Detail(compound, $"WindowBar_{side}_{bay}_{bar}",
                        Along(centre + Mathf.Lerp(-0.9f, 0.9f, t), sill),
                        Size(0.09f, 1.0f, 0.2f), _rafter);
                }

                Detail(compound, $"WindowSill_{side}_{bay}", Along(centre, sill - 0.62f),
                    Size(2.4f, 0.14f, 0.3f), _darkTimber);
            }

            // Fascia board along the eave, which is what gives the roofline its dark edge.
            Detail(compound, $"Fascia_{side}",
                horizontal
                    ? new Vector3(0f, height + 0.02f, (half + EaveOverhang) * sign)
                    : new Vector3((half + EaveOverhang) * sign, height + 0.02f, 0f),
                Size(length + EaveOverhang * 2f, 0.34f, 0.18f), _darkTimber);
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

                // The lowest tier is the one anybody sees from a street, so the sweep goes there.
                if (i == 0) BuildCurvedEave(compound, span, y, $"Hip_{i}");

                y += riser;
            }

            float top = y - riser + slabThickness * 0.5f;

            // A ridge along the top with an ornament at either end. A hip roof that just stops is
            // the shape of a shed; the ridge and its end tiles are what make it a building
            // somebody cared about.
            BuildRidge(compound, eaveSpan - inset * 2f * (tiers - 1), top, "Hip");

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

            // Hip ridges running from the crown down to each corner. On a real roof these are the
            // heaviest line in the whole silhouette - the tiled spine capping the join between two
            // pitches - and without them a hipped roof reads as a stack of trays.
            float run = (eaveSpan - crown * 0.55f) * 0.5f;
            float diagonal = Mathf.Sqrt(run * run * 2f) * 0.72f;

            for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                float mid = (crown * 0.55f * 0.5f + eaveSpan * 0.5f) * 0.5f;

                Transform hip = Detail(compound, $"HipRidge_{sx}_{sz}",
                    new Vector3(sx * mid * 0.72f, top - riser * 1.6f, sz * mid * 0.72f),
                    new Vector3(diagonal, 0.34f, 0.62f), _darkTimber);

                hip.localRotation = Quaternion.Euler(0f, sx * sz > 0 ? 45f : -45f, 0f);
            }

            for (int sx = -1; sx <= 1; sx += 2)
                Detail(compound, $"Onigawara_{sx}",
                    new Vector3(sx * crown * 0.28f, top + 0.34f, 0f),
                    new Vector3(0.9f, 0.66f, 1.1f), _darkTimber);

            return top;
        }

        /// <summary>
        /// An engawa: the raised timber verandah that runs along the outside of a room, between
        /// the shoji and the garden.
        ///
        /// Gameplay-wise it is a narrow raised walkway with a lip you can crouch behind, hard
        /// against the paper walls - which is the best place in a compound to listen from and the
        /// worst place to be caught standing.
        /// </summary>
        private void BuildEngawa(Transform compound, float half, int doorSide)
        {
            // Along the side opposite the door, so it faces the quiet part of the plot.
            int side = (doorSide + 2) % 4;
            bool horizontal = side % 2 == 0;
            float sign = side < 2 ? 1f : -1f;
            float inset = half - 2.6f;

            Transform deck = Box(compound, "Engawa",
                horizontal ? new Vector3(0f, 0.55f, inset * sign) : new Vector3(inset * sign, 0.55f, 0f),
                horizontal
                    ? new Vector3(half * 1.5f, 0.3f, 2.2f)
                    : new Vector3(2.2f, 0.3f, half * 1.5f),
                UnseenLayers.Default, _woodFloor);
            Acoustics(deck, 0.55f, 0.8f, 0.9f);

            // Boarded skirt, so the deck has an underside rather than floating.
            Detail(compound, "EngawaSkirt",
                horizontal
                    ? new Vector3(0f, 0.2f, (inset + 1.05f) * sign)
                    : new Vector3((inset + 1.05f) * sign, 0.2f, 0f),
                horizontal
                    ? new Vector3(half * 1.5f, 0.4f, 0.14f)
                    : new Vector3(0.14f, 0.4f, half * 1.5f),
                _darkTimber);

            // Posts carrying the eave above it.
            for (int i = -1; i <= 1; i += 2)
            {
                Detail(compound, $"EngawaPost_{i}",
                    horizontal
                        ? new Vector3(half * 0.62f * i, 1.9f, (inset + 0.9f) * sign)
                        : new Vector3((inset + 0.9f) * sign, 1.9f, half * 0.62f * i),
                    new Vector3(0.2f, 2.4f, 0.2f), _darkTimber);
            }
        }

        /// <summary>
        /// A run of shoji panels along one axis, each an independent destructible.
        ///
        /// The kumiko lattice is in the paper texture rather than modelled, for the reason given
        /// where that material is built: three thousand panels times a dozen muntins is not a trade
        /// worth making for flat regular detail. What IS worth modelling is the joinery that has
        /// depth and catches light - the head and sill tracks the panels slide in, and a rail at the
        /// top as well as the bottom, which is two boxes per run and one per panel.
        /// </summary>
        private void BuildShojiRun(Transform parent, Vector3 centre, float length, bool alongX, float height)
        {
            const float panelWidth = 2.6f;
            int panels = Mathf.Max(1, Mathf.RoundToInt(length / panelWidth));
            float actual = length / panels;

            // Kamoi and shikii: the grooved head and sill the panels run in. One of each for the
            // whole run rather than one per panel, because that is what they are.
            for (int rail = 0; rail < 2; rail++)
            {
                float y = rail == 0 ? -height * 0.5f + 0.09f : height * 0.44f;
                float thick = rail == 0 ? 0.22f : 0.18f;

                Vector3 railSize = alongX
                    ? new Vector3(length, thick, 0.26f)
                    : new Vector3(0.26f, thick, length);

                Transform track = Detail(parent, $"ShojiTrack_{(alongX ? "X" : "Z")}_{rail}",
                    centre + new Vector3(0f, y, 0f), railSize, _darkTimber);

                // A shallow lip on the outer face, so the track reads as grooved rather than as a
                // plain beam.
                Vector3 lipSize = alongX
                    ? new Vector3(length, thick * 0.35f, 0.3f)
                    : new Vector3(0.3f, thick * 0.35f, length);

                Detail(parent, $"ShojiLip_{(alongX ? "X" : "Z")}_{rail}",
                    centre + new Vector3(0f, y + thick * 0.32f, 0f), lipSize, _timber);
            }

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

                // A top rail to match the bottom one, and a pull recessed into the leading stile.
                // One box each; the stiles either side are drawn by the paper texture's frame band.
                Detail(panelHost, "TopRail", new Vector3(0f, height * 0.43f, 0f),
                    new Vector3(actual, 0.14f, 0.14f), _timber);

                Detail(panelHost, "Pull",
                    new Vector3(actual * 0.42f, height * 0.06f, 0.055f),
                    new Vector3(0.1f, 0.28f, 0.03f), _darkTimber);

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

            _sketch?.Add(MapSketch.Feature.Keep, origin,
                new Vector2(BlockSize * 0.45f, BlockSize * 0.45f));

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

        // ---------------------------------------------------------------- water gardens

        /// <summary>Half-width of the stone island the keep stands on, in metres.</summary>
        private const float IslandHalf = 15.5f;

        /// <summary>Half-width of the moat's outer wall.</summary>
        private const float MoatHalf = 23f;

        /// <summary>How high the island stands above the street.</summary>
        private const float PlinthHeight = 1.6f;

        private Material _lakeWater;
        private Material _wetStone;

        /// <summary>
        /// Rock that water runs over, and rock standing in it.
        ///
        /// The town's stone textures are all pale - they were cut for walls and paving, which in
        /// this town are lime and granite - and a five metre boulder wearing a pale gravel texture
        /// reads as a heap of crumpled paper rather than as rock. Wet stone is dark, so this is
        /// the same material taken down to about a third of its brightness, which is roughly what
        /// water does to a rock face.
        /// </summary>
        private Material WetStone()
        {
            if (_wetStone != null) return _wetStone;

            Material source = _riverStone != null ? _riverStone : _stone;
            if (source == null) return null;

            _wetStone = new Material(source) { name = "WetStone" };

            // A THIRD of the gravel's own tint, not a third of white. The river stone material is
            // already dark at 0.22 and its base map is bright, so a tint of 0.29 - which is what
            // this was - made the rock lighter than the wall behind it. Measured, not guessed:
            // Unseen > Probe Water Gardens prints the base colour of every material in the lake.
            var tint = new Color(0.11f, 0.12f, 0.13f);

            if (_wetStone.HasProperty("_BaseColor")) _wetStone.SetColor("_BaseColor", tint);
            if (_wetStone.HasProperty("_Color")) _wetStone.SetColor("_Color", tint);

            // Smoother than dry stone, though. Wet rock is dark AND shiny, and dark on its own
            // just reads as a hole.
            if (_wetStone.HasProperty("_Smoothness"))
                _wetStone.SetFloat("_Smoothness", 0.58f);

            return _wetStone;
        }

        /// <summary>
        /// The moat round the castle, and everything living in it.
        ///
        /// Built UP rather than dug down. The ground is laid as flat slabs and the only hole ever
        /// cut in one is the river channel, so excavating a moat would mean cutting the town's
        /// floor apart for one building. A castle stands on a stone base anyway - the ishigaki -
        /// so raising the island and walling the water in around it is both easier and more
        /// correct than sinking it.
        ///
        /// The water is chest deep on the way across. That is a real decision at the centre of the
        /// map: the two bridges are the fast way in and the obvious place to be watched from, and
        /// wading is slow, loud, and puts your head under if you go prone - which is also the way
        /// to cross unseen.
        ///
        /// Returns how high the island stands, so the keep can be built on top of it.
        /// </summary>
        private float BuildCastleLake(Vector3 origin)
        {
            var lake = new GameObject("CastleLake").transform;
            lake.SetParent(_root, false);
            lake.localPosition = origin;

            float waterY = PlinthHeight - 0.25f;

            // ------------------------------------------------------------ the island
            Transform island = Box(lake, "Island", new Vector3(0f, PlinthHeight * 0.5f, 0f),
                new Vector3(IslandHalf * 2f, PlinthHeight, IslandHalf * 2f),
                UnseenLayers.Default, _stone);
            Acoustics(island, 0.9f, 1.1f, 1.1f);

            // Battered stone facing. A vertical wall is a retaining wall; the slope is what makes
            // it a castle base, and it is what everybody recognises the shape from.
            for (int course = 0; course < 3; course++)
            {
                float t = course / 3f;
                float out_ = 0.55f * (1f - t);
                float y = PlinthHeight * (0.12f + t * 0.32f);

                for (int side = 0; side < 4; side++)
                {
                    bool horizontal = side % 2 == 0;
                    float sign = side < 2 ? 1f : -1f;
                    float reach = IslandHalf + out_;

                    Detail(lake, $"Batter_{course}_{side}",
                        horizontal
                            ? new Vector3(0f, y, reach * sign)
                            : new Vector3(reach * sign, y, 0f),
                        horizontal
                            ? new Vector3(IslandHalf * 2f + out_ * 2f, PlinthHeight * 0.3f, 0.3f)
                            : new Vector3(0.3f, PlinthHeight * 0.3f, IslandHalf * 2f + out_ * 2f),
                        _stone);
                }
            }

            // ------------------------------------------------------------ the outer wall
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;

                Transform wall = Box(lake, $"MoatWall_{side}",
                    horizontal
                        ? new Vector3(0f, PlinthHeight * 0.5f, MoatHalf * sign)
                        : new Vector3(MoatHalf * sign, PlinthHeight * 0.5f, 0f),
                    horizontal
                        ? new Vector3(MoatHalf * 2f + 1.2f, PlinthHeight, 1.2f)
                        : new Vector3(1.2f, PlinthHeight, MoatHalf * 2f + 1.2f),
                    UnseenLayers.Occluder, _stone);
                Acoustics(wall, 0.9f, 1.1f, 1.1f);
            }

            // ------------------------------------------------------------ the water
            //
            // One flat surface across the whole moat, island included. The island stands proud of
            // it, so the part underneath is never seen and costs one quad to leave there rather
            // than four strips to cut it out.
            Transform surface = Detail(lake, "LakeWater",
                new Vector3(0f, waterY - 0.08f, 0f),
                new Vector3(MoatHalf * 2f, 0.16f, MoatHalf * 2f), LakeWater());

            surface.gameObject.AddComponent<WaterVolume>().Configure(
                waterY + origin.y,
                new Vector2(MoatHalf, MoatHalf),
                waterY - 0.1f,
                new Vector2(IslandHalf, IslandHalf));

            // ------------------------------------------------------------ two ways across
            //
            // Two, on opposite sides. One would make the approach a single chokepoint that decides
            // every fight at the centre of the map before it starts; three would make the moat
            // decorative. Two is a choice.
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float span = MoatHalf - IslandHalf;
                float mid = (MoatHalf + IslandHalf) * 0.5f;

                // A shallow arch, in three planks, so it reads as a bridge rather than a plank.
                for (int i = 0; i < 3; i++)
                {
                    float t = (i - 1) / 1f;
                    float rise = 0.42f * (1f - t * t);

                    Transform deck = Box(lake, $"MoatBridge_{sign}_{i}",
                        new Vector3(0f, PlinthHeight + rise, (mid + t * span * 0.33f) * sign),
                        new Vector3(3.4f, 0.3f, span * 0.42f),
                        UnseenLayers.Default, _darkTimber);
                    Acoustics(deck, 0.5f, 1.5f, 1.3f);
                }

                for (int rail = -1; rail <= 1; rail += 2)
                {
                    Detail(lake, $"MoatRail_{sign}_{rail}",
                        new Vector3(1.6f * rail, PlinthHeight + 0.85f, mid * sign),
                        new Vector3(0.14f, 0.14f, span), _vermilion);

                    for (int post = 0; post < 3; post++)
                    {
                        float t = (post - 1) / 1f;
                        Detail(lake, $"MoatPost_{sign}_{rail}_{post}",
                            new Vector3(1.6f * rail,
                                PlinthHeight + 0.45f,
                                (mid + t * span * 0.34f) * sign),
                            new Vector3(0.16f, 0.9f, 0.16f), _vermilion);
                    }
                }

                PostLantern(lake, new Vector3(2.1f * sign, PlinthHeight, mid * sign), 1.9f, 9f, 1f);
            }

            // ------------------------------------------------------------ rocks in the water
            //
            // Some breaking the surface, some not. A pond of uniform depth with nothing in it is a
            // swimming bath, and it is the ones half under that say how deep the water is.
            for (int i = 0; i < 22; i++)
            {
                float angle = i * 2.399f;
                float lane = (float)_random.NextDouble();
                float reach = Mathf.Max(Mathf.Abs(Mathf.Cos(angle)), Mathf.Abs(Mathf.Sin(angle)));
                float band = Mathf.Lerp(IslandHalf + 1.1f, MoatHalf - 1.1f, lane) /
                             Mathf.Max(0.35f, reach);

                float size = 0.6f + (float)_random.NextDouble() * 1.5f;
                float sink = (float)_random.NextDouble();

                Organic(lake, $"MoatRock_{i}",
                    OrganicMeshFactory.Blob(7, 12, 0.3f, i % 8),
                    new Vector3(Mathf.Cos(angle) * band,
                        waterY - size * (0.15f + sink * 0.55f),
                        Mathf.Sin(angle) * band),
                    new Vector3(size * 1.3f, size, size * 1.15f),
                    WetStone());
            }

            // Reeds against the island, where silt collects.
            for (int i = 0; i < 30; i++)
            {
                float angle = i * 1.257f;
                float reach = Mathf.Max(Mathf.Abs(Mathf.Cos(angle)), Mathf.Abs(Mathf.Sin(angle)));
                float band = (IslandHalf + 0.55f) / Mathf.Max(0.35f, reach);
                float tall = 0.7f + (float)_random.NextDouble() * 0.8f;

                Organic(lake, $"MoatReed_{i}",
                    OrganicMeshFactory.Blade(4, 0.5f),
                    new Vector3(Mathf.Cos(angle) * band, waterY - 0.15f, Mathf.Sin(angle) * band),
                    new Vector3(0.5f, tall, 0.5f), _reed)
                    .localRotation = Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f);
            }

            // ------------------------------------------------------------ the fish
            for (int i = 0; i < 9; i++)
            {
                Transform fish = BuildKoiBody(lake, i);

                fish.gameObject.AddComponent<Koi>().Configure(
                    origin + new Vector3(0f, 0f, 0f),
                    new Vector2(IslandHalf + 1.6f, MoatHalf - 1.6f),
                    origin.y + waterY,
                    i,
                    square: true);

                _koi++;
            }

            // ------------------------------------------------------------ the water comes from
            //                                                              somewhere
            //
            // A moat with no inlet is a tank. The fall is on one corner, outside the wall, and it
            // is the loudest thing at the centre of the map - which makes the whole approach to
            // the keep quieter to cross than it looks.
            // In the corner of the moat itself, against the outer wall, so it pours into the
            // water rather than beside it.
            BuildWaterfall(lake, new Vector3(-MoatHalf + 3.2f, 0f, MoatHalf - 3.2f),
                new Vector3(0.7f, 0f, -0.7f), 4.4f, 3.8f, waterY);

            _gardens++;
            return PlinthHeight;
        }

        /// <summary>
        /// One carp: a body, a tail and two fins.
        ///
        /// Small and seen through moving water from several metres up, so the whole fish is four
        /// pieces. What identifies it is the shape of the silhouette and the fact that it is
        /// slowly turning, not the modelling.
        /// </summary>
        private Transform BuildKoiBody(Transform parent, int index)
        {
            var fish = new GameObject($"Koi_{index}").transform;
            fish.SetParent(parent, false);

            // Carp come in white, orange and near-black, and the pale ones are the only ones
            // visible through the water at all - so most of them are pale.
            Material skin = index % 3 == 0 ? _darkTimber : (index % 3 == 1 ? _vermilion : _plaster);

            Organic(fish, "Body", OrganicMeshFactory.Blob(5, 8, 0.18f, index % 8),
                Vector3.zero, new Vector3(0.16f, 0.13f, 0.46f), skin);

            Organic(fish, "Tail", OrganicMeshFactory.Blade(3, 0.4f),
                new Vector3(0f, 0.02f, -0.3f), new Vector3(0.2f, 0.2f, 0.2f), skin)
                .localRotation = Quaternion.Euler(-72f, 0f, 0f);

            for (int side = -1; side <= 1; side += 2)
            {
                Organic(fish, $"Fin_{side}", OrganicMeshFactory.Blade(3, 0.3f),
                    new Vector3(0.07f * side, -0.02f, 0.04f),
                    new Vector3(0.14f, 0.14f, 0.14f), skin)
                    .localRotation = Quaternion.Euler(-80f, 0f, 55f * side);
            }

            return fish;
        }

        /// <summary>
        /// A waterfall: a rock face, a sheet of falling water, a plunge pool and the mist off it.
        ///
        /// The sheet is the water material stood on its end. That is not a cheat - the shader
        /// scrolls its waves along the surface, and a vertical surface scrolling downward is
        /// exactly what falling water is - and it means the fall has the same colour and movement
        /// as everything else wet in the town instead of being a white box.
        /// </summary>
        private void BuildWaterfall(Transform parent, Vector3 at, Vector3 outward,
            float height, float width, float poolY)
        {
            var fall = new GameObject("Waterfall").transform;
            fall.SetParent(parent, false);
            fall.localPosition = at;

            Vector3 back = -outward.normalized;
            Vector3 across = Vector3.Cross(Vector3.up, outward.normalized);

            // The cliff. One box does the colliding, because cover in this game has to be a
            // shape a player can predict from looking at it, and the rock masses on top of it are
            // what they actually see.
            Transform block = Box(fall, "Crag", back * 1.1f + Vector3.up * (height * 0.5f),
                new Vector3(width * 0.95f, height, 2.6f), UnseenLayers.Occluder, _riverStone);
            Acoustics(block, 0.95f, 1f, 1f);

            var blockRenderer = block.GetComponent<MeshRenderer>();
            if (blockRenderer != null) UnseenObject.Destroy(blockRenderer);

            // Masses stepping BACK as they rise, hung on the collider so they cannot drift off
            // it. Wide enough to cover the collider - a fall coming off the edge of an invisible
            // wall is worse than no fall - and no wider, because a boulder taller than the
            // building behind it stops being scenery and becomes the subject.
            Vector3 localBack = block.InverseTransformDirection(back);

            for (int i = 0; i < 6; i++)
            {
                float t = i / 5f;
                float size = Mathf.Lerp(width * 1.05f, width * 0.6f, t);

                Organic(block,
                    localBack * (t * 1.1f) +
                    new Vector3(
                        Mathf.Sin(i * 2.1f) * width * 0.12f,
                        height * (t - 0.5f) * 0.94f,
                        0f),
                    new Vector3(size, height * 0.34f, size * 0.85f),
                    WetStone(),
                    OrganicMeshFactory.Blob(7, 12, 0.3f, i % 8));
            }

            // The sheet. Renderer only - falling water is not a floor, and a collider here would
            // let people stand halfway up it.
            //
            // The flat face has to point OUT of the cliff. Turned the other way it is a thin blue
            // stripe seen edge-on, which is a rod of water rather than a fall, and the difference
            // is one axis.
            Transform sheet = Detail(fall, "Sheet",
                outward.normalized * (width * 0.16f) +
                Vector3.up * (height * 0.5f + poolY * 0.5f),
                new Vector3(width * 0.82f, height - poolY, 0.22f), FallWater());
            sheet.localRotation = Quaternion.LookRotation(outward.normalized, Vector3.up);

            // A lip at the top, so the water comes over something instead of starting in mid-air.
            Detail(fall, "Lip", Vector3.up * (height + 0.05f) + outward.normalized * 0.35f,
                new Vector3(width * 0.9f, 0.22f, 1.1f), WetStone());

            // Spray where it lands, and mist drifting off the pool. Both are the ground-mist
            // material, which already drifts and already fades with distance.
            if (_groundMist != null)
            {
                for (int i = 0; i < 7; i++)
                {
                    float t = i / 6f;

                    Transform puff = Detail(fall, $"Spray_{i}",
                        Vector3.up * (poolY + 0.3f + t * 1.5f) +
                        across * ((float)(_random.NextDouble() * 2f - 1f) * width * 0.6f) +
                        outward.normalized * ((float)_random.NextDouble() * 1.6f),
                        new Vector3(width * (1f + t), 0.06f, width * (0.7f + t * 0.6f)),
                        _groundMist);

                    var renderer = puff.GetComponent<MeshRenderer>();
                    if (renderer != null)
                        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }

            // Rocks at the foot, broken off the face.
            for (int i = 0; i < 6; i++)
            {
                float size = 0.4f + (float)_random.NextDouble() * 0.9f;

                Organic(fall, $"Scree_{i}",
                    OrganicMeshFactory.Blob(6, 10, 0.34f, (i + 3) % 8),
                    outward.normalized * (0.8f + (float)_random.NextDouble() * 2.2f) +
                    across * ((float)(_random.NextDouble() * 2f - 1f) * width * 0.8f) +
                    Vector3.up * (poolY - size * 0.3f),
                    new Vector3(size * 1.3f, size, size), WetStone());
            }

            _waterfalls++;
        }

        /// <summary>
        /// A karesansui: raked gravel, a handful of standing stones, moss, and a wall round it.
        ///
        /// The whole point of one of these is that it is EMPTY - a rectangle of gravel with five
        /// stones in it and nothing else - which makes it the one place in a dense town with a
        /// clear sightline across it, and therefore the last place anybody sensible walks through.
        /// It earns its place in a stealth game by being beautiful and lethal at the same time.
        ///
        /// The rake lines are thin slabs a few centimetres proud of the gravel. At any distance
        /// they are the pattern; up close they are ridges, which is what raked gravel actually is.
        /// </summary>
        private void BuildZenGarden(Vector3 origin, int salt)
        {
            var garden = new GameObject("ZenGarden").transform;
            garden.SetParent(_root, false);
            garden.localPosition = origin;

            var rng = new System.Random(salt * 7919 + Seed);

            float half = BlockSize * 0.36f;

            _sketch?.Add(MapSketch.Feature.Garden, origin, new Vector2(half, half));

            // The gravel bed, very slightly raised, so it has an edge.
            Transform bed = Box(garden, "Gravel", new Vector3(0f, 0.06f, 0f),
                new Vector3(half * 2f, 0.12f, half * 2f), UnseenLayers.Default, _riverStone);
            Acoustics(bed, 0.6f, 1.6f, 1.5f);

            // The wall. Low enough to see over standing, high enough to hide behind crouched,
            // which is the only wall height that matters in this game.
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                float reach = half + 0.4f;

                Transform wall = Box(garden, $"GardenWall_{side}",
                    horizontal
                        ? new Vector3(0f, 0.6f, reach * sign)
                        : new Vector3(reach * sign, 0.6f, 0f),
                    horizontal
                        ? new Vector3(half * 2f + 0.8f, 1.2f, 0.36f)
                        : new Vector3(0.36f, 1.2f, half * 2f + 0.8f),
                    UnseenLayers.Occluder, _plaster);
                Acoustics(wall, 0.85f, 1f, 1f);

                Detail(garden, $"GardenCap_{side}",
                    horizontal
                        ? new Vector3(0f, 1.26f, reach * sign)
                        : new Vector3(reach * sign, 1.26f, 0f),
                    horizontal
                        ? new Vector3(half * 2f + 1.1f, 0.14f, 0.62f)
                        : new Vector3(0.62f, 0.14f, half * 2f + 1.1f),
                    _tile);
            }

            // Five stones in three groups, which is the arrangement. Placed off-centre and never
            // evenly, because an even one reads as a car park.
            var groups = new[]
            {
                new Vector3(-half * 0.42f, 0f, half * 0.22f),
                new Vector3(half * 0.3f, 0f, -half * 0.36f),
                new Vector3(half * 0.5f, 0f, half * 0.48f)
            };

            int stone = 0;

            for (int g = 0; g < groups.Length; g++)
            {
                int inGroup = g == 0 ? 2 : (g == 1 ? 2 : 1);

                for (int i = 0; i < inGroup; i++)
                {
                    float size = 0.7f + (float)rng.NextDouble() * 1.3f;
                    Vector3 nudge = new Vector3(
                        (float)(rng.NextDouble() * 2f - 1f) * 1.2f, 0f,
                        (float)(rng.NextDouble() * 2f - 1f) * 1.2f);

                    Vector3 at = groups[g] + nudge;

                    Organic(garden, $"Stone_{stone}",
                        OrganicMeshFactory.Blob(7, 12, 0.3f, stone % 8),
                        at + Vector3.up * (0.12f + size * 0.32f),
                        new Vector3(size * 1.1f, size, size * 0.9f), _riverStone);

                    // Moss at the foot of each one, where the rain runs off it.
                    Organic(garden, $"Moss_{stone}",
                        OrganicMeshFactory.Blob(4, 8, 0.5f, (stone + 4) % 8),
                        at + Vector3.up * 0.13f,
                        new Vector3(size * 2.1f, 0.16f, size * 1.9f), _moss);

                    // Raked rings around the group: the gravel is combed AROUND the stones, which
                    // is the detail that makes a rock in gravel read as placed rather than dropped.
                    for (int ring = 1; ring <= 3; ring++)
                    {
                        float radius = size * (0.9f + ring * 0.55f);
                        int segments = Mathf.Clamp(Mathf.RoundToInt(radius * 5f), 8, 30);

                        for (int seg = 0; seg < segments; seg++)
                        {
                            float a = seg / (float)segments * Mathf.PI * 2f;

                            Detail(garden, $"Ripple_{stone}_{ring}_{seg}",
                                at + new Vector3(Mathf.Cos(a) * radius, 0.14f,
                                    Mathf.Sin(a) * radius),
                                new Vector3(0.26f, 0.05f, 0.26f), _plaster);
                        }
                    }

                    stone++;
                }
            }

            // And straight rakes across the open gravel, which is the rest of the pattern.
            int lines = 11;
            for (int i = 0; i < lines; i++)
            {
                float t = (i + 0.5f) / lines;
                float z = Mathf.Lerp(-half * 0.92f, half * 0.92f, t);

                Detail(garden, $"Rake_{i}", new Vector3(0f, 0.14f, z),
                    new Vector3(half * 1.84f, 0.05f, 0.16f), _plaster);
            }

            // One stone lantern, at a corner, which is where they go.
            PostLantern(garden, new Vector3(-half * 0.78f, 0.12f, -half * 0.78f), 1.5f, 8f, 0.85f);

            _zenGardens++;
            _gardens++;
        }

        /// <summary>
        /// A rock garden built round a waterfall: a mound of boulders, a pool at the foot, and
        /// pines growing out of the gaps.
        ///
        /// The opposite of the raked garden in every way that matters to a player. That one is
        /// open ground you cross at your peril; this one is broken cover, high ground, and the
        /// loudest place in the district - the fall drowns footsteps for ten metres around it,
        /// which makes it the best ambush in the town and the worst place to be ambushed.
        /// </summary>
        private void BuildRockGarden(Vector3 origin, int salt)
        {
            var garden = new GameObject("RockGarden").transform;
            garden.SetParent(_root, false);
            garden.localPosition = origin;

            var rng = new System.Random(salt * 6151 + Seed);

            float half = BlockSize * 0.34f;
            float poolY = 0.55f;

            _sketch?.Add(MapSketch.Feature.Garden, origin, new Vector2(half, half));

            // The pool. Walled rather than dug, same reason as the moat.
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side % 2 == 0;
                float sign = side < 2 ? 1f : -1f;
                float reach = half * 0.55f;

                Transform kerb = Box(garden, $"PoolKerb_{side}",
                    horizontal
                        ? new Vector3(0f, poolY * 0.5f + 0.1f, reach * sign)
                        : new Vector3(reach * sign, poolY * 0.5f + 0.1f, 0f),
                    horizontal
                        ? new Vector3(half * 1.1f + 0.9f, poolY + 0.2f, 0.9f)
                        : new Vector3(0.9f, poolY + 0.2f, half * 1.1f + 0.9f),
                    UnseenLayers.Default, _riverStone);
                Acoustics(kerb, 0.8f, 1.2f, 1.1f);
            }

            Detail(garden, "PoolWater", new Vector3(0f, poolY - 0.05f, 0f),
                new Vector3(half * 1.1f, 0.14f, half * 1.1f), LakeWater());

            // Stepping stones across it. Shallow enough to walk, which is the point of a garden
            // pool as opposed to a moat.
            for (int i = 0; i < 4; i++)
            {
                float t = (i + 0.5f) / 4f;

                Organic(garden, $"Stepping_{i}",
                    OrganicMeshFactory.Blob(5, 9, 0.3f, i % 8),
                    new Vector3(Mathf.Lerp(-half * 0.45f, half * 0.45f, t), poolY - 0.1f,
                        (float)(rng.NextDouble() * 2f - 1f) * 0.7f),
                    new Vector3(1.1f, 0.5f, 1.0f), _riverStone);
            }

            // The mound: boulders in decreasing size going up, so it has a summit you can climb.
            for (int i = 0; i < 14; i++)
            {
                float t = i / 13f;
                float angle = i * 2.399f;
                float band = Mathf.Lerp(half * 0.85f, half * 0.3f, t);
                float size = Mathf.Lerp(2.4f, 0.9f, t) * (0.7f + (float)rng.NextDouble() * 0.6f);

                var at = new Vector3(
                    Mathf.Cos(angle) * band,
                    0.2f + t * 2.6f,
                    Mathf.Sin(angle) * band + half * 0.55f);

                Transform block = Box(garden, $"Boulder_{i}", at,
                    new Vector3(size * 1.2f, size, size * 1.1f),
                    UnseenLayers.Occluder, _riverStone);
                Acoustics(block, 0.9f, 1.1f, 1f);

                // The collider is a box because cover has to be predictable; the shape on top of
                // it is not, because a boulder is not.
                var renderer = block.GetComponent<MeshRenderer>();
                if (renderer != null) UnseenObject.Destroy(renderer);

                // Stone, not river gravel. The gravel texture is a field of small pale chips,
                // which at boulder scale reads as crumpled paper rather than rock.
                Organic(block, "Mass", OrganicMeshFactory.Blob(7, 12, 0.28f, i % 8),
                    Vector3.zero, new Vector3(size * 1.5f, size * 1.25f, size * 1.35f),
                    WetStone());

                if (rng.NextDouble() < 0.5)
                    Organic(block, "Moss", OrganicMeshFactory.Blob(4, 8, 0.5f, (i + 2) % 8),
                        new Vector3(0f, size * 0.5f, 0f),
                        new Vector3(size * 1.2f, size * 0.3f, size * 1.1f), _moss);
            }

            // The fall, off the back of the mound into the pool.
            BuildWaterfall(garden, new Vector3(0f, 0f, half * 0.62f), Vector3.back,
                4.2f, 3.2f, poolY);

            // Pines specifically, not whatever the general tree roll comes up with. A stand of
            // bamboo in here swamps the rock work it is supposed to be growing out of, and the
            // rocks are the garden.
            for (int i = 0; i < 5; i++)
            {
                float angle = i * 1.9f + 0.4f;

                var stem = new GameObject($"Pine_{i}").transform;
                stem.SetParent(garden, false);
                stem.localPosition = new Vector3(
                    Mathf.Cos(angle) * half * 0.78f, 0f,
                    Mathf.Sin(angle) * half * 0.78f - half * 0.25f);

                BuildPine(stem, 3.4f + (float)rng.NextDouble() * 2.6f,
                    1.5f + (float)rng.NextDouble() * 0.9f);
            }

            for (int i = 0; i < 12; i++)
            {
                Organic(garden, $"GroundMoss_{i}",
                    OrganicMeshFactory.Blob(4, 8, 0.55f, i % 8),
                    new Vector3((float)(rng.NextDouble() * 2f - 1f) * half,
                        0.04f,
                        (float)(rng.NextDouble() * 2f - 1f) * half),
                    new Vector3(1.4f + (float)rng.NextDouble() * 2f, 0.12f,
                        1.3f + (float)rng.NextDouble() * 2f),
                    _moss);
            }

            PostLantern(garden, new Vector3(half * 0.7f, 0f, -half * 0.6f), 1.6f, 9f, 0.9f);

            _rockGardens++;
            _gardens++;
        }

        /// <summary>
        /// The water material for still water, as opposed to the river.
        ///
        /// A separate instance because the river's shader colours itself by distance from the
        /// channel centre - it has to, that is how the shallows show gravel and the middle goes
        /// dark - and a pond eighty metres from that centre would render as the far bank of a
        /// river it is not in. This copy is told it is deep everywhere.
        /// </summary>
        private Material _fallWater;

        /// <summary>
        /// Falling water, as opposed to standing water.
        ///
        /// The same shader told the opposite thing about its depth. Still water is deep and
        /// therefore dark and opaque; a sheet coming off a rock is a few centimetres thick, and
        /// shallow water in this shader is pale and half transparent - which is exactly what a
        /// fall looks like, and the only way it is visible at all against wet rock at night.
        /// </summary>
        private Material FallWater()
        {
            if (_fallWater != null) return _fallWater;

            Material source = LakeWater();
            if (source == null) return null;

            _fallWater = new Material(source) { name = "FallingWater" };

            if (_fallWater.HasProperty("_DeepHalf"))
            {
                // Inside the channel everywhere, deep nowhere.
                _fallWater.SetFloat("_ChannelHalf", 10000f);
                _fallWater.SetFloat("_DeepHalf", 0f);
                _fallWater.SetFloat("_ShallowDepth", 0.06f);
                _fallWater.SetFloat("_DeepDepth", 0.1f);
            }

            return _fallWater;
        }

        private Material LakeWater()
        {
            if (_lakeWater != null) return _lakeWater;
            if (_water == null) return _stone;

            _lakeWater = new Material(_water) { name = "LakeWater" };

            if (_lakeWater.HasProperty("_ChannelCentre"))
            {
                _lakeWater.SetFloat("_ChannelCentre", 0f);
                _lakeWater.SetFloat("_ChannelHalf", 10000f);
                _lakeWater.SetFloat("_DeepHalf", 10000f);
                _lakeWater.SetFloat("_ShallowDepth", 0.9f);
                _lakeWater.SetFloat("_DeepDepth", 1.5f);
            }

            return _lakeWater;
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
            // Nothing to hang it FROM means no lantern.
            //
            // This traced upward for a beam and, when the trace found nothing, fell back to a
            // guessed 1.2 m cord - which drew a rope from the lantern up into thin air. Reported
            // from play as lanterns attached to nothing, and it is exactly that: the fallback was
            // hiding a failed search instead of reporting it.
            Vector3 world = parent.TransformPoint(position + Vector3.up * 0.32f);

            if (!Physics.Raycast(world, Vector3.up, out RaycastHit hit, 6f,
                    UnseenLayers.WorldGeometry | (1 << UnseenLayers.Rafter),
                    QueryTriggerInteraction.Ignore))
            {
                // No beam. Stand it on the floor underneath instead of dropping the light
                // altogether - a pagoda balcony wants a lantern whether or not it has a rafter,
                // and a hundred and ten of these were being asked for.
                if (Physics.Raycast(world, Vector3.down, out RaycastHit floor, 4f,
                        UnseenLayers.WorldGeometry, QueryTriggerInteraction.Ignore) &&
                    Vector3.Dot(floor.normal, Vector3.up) > 0.6f)
                {
                    Vector3 local = parent.InverseTransformPoint(floor.point);
                    PostLantern(parent, local, 1.9f, radius, intensity);
                    _stoodLanterns++;
                    return;
                }

                _unhungLanterns++;
                return;
            }

            CreateLantern(parent, position, radius, intensity);

            float drop = Mathf.Max(0.2f, hit.distance);
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
                _vermilion = set.Vermilion != null ? set.Vermilion : set.Timber;
                _bamboo = set.Bamboo != null ? set.Bamboo : set.Foliage;
                _grass = set.Grass != null ? set.Grass : set.Foliage;
                _dirt = set.Dirt != null ? set.Dirt : set.Ground;
                _reed = set.Reed != null ? set.Reed : set.Foliage;
                _riverStone = set.RiverStone != null ? set.RiverStone : set.Stone;
                _groundMist = set.GroundMist;
                _moss = set.Moss != null ? set.Moss : set.Foliage;
                _bambooMass = set.BambooMass != null ? set.BambooMass : set.Foliage;
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
            _vermilion = MakeMaterial(shader, "GreyboxVermilion", new Color(0.62f, 0.17f, 0.12f));
            _bamboo = MakeMaterial(shader, "GreyboxBamboo", new Color(0.32f, 0.38f, 0.20f));
            _grass = MakeMaterial(shader, "GreyboxGrass", new Color(0.2f, 0.28f, 0.14f));
            _dirt = MakeMaterial(shader, "GreyboxDirt", new Color(0.32f, 0.25f, 0.18f));
            _reed = MakeMaterial(shader, "GreyboxReed", new Color(0.3f, 0.36f, 0.18f));
            _riverStone = MakeMaterial(shader, "GreyboxRiverStone", new Color(0.24f, 0.25f, 0.24f));
            _moss = MakeMaterial(shader, "GreyboxMoss", new Color(0.14f, 0.22f, 0.10f));
            _bambooMass = MakeMaterial(shader, "GreyboxBambooMass", new Color(0.12f, 0.17f, 0.10f));
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
