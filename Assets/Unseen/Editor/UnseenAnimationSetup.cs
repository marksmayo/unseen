using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Authors the combat animation set in code, and wires it into the ninja's animator as a
    /// masked upper-body layer.
    ///
    /// Authored procedurally because the character set ships three clips - idle, run, jump - and
    /// the game needs a swing, a guard, a flinch and a takedown. Every pose here is expressed as a
    /// rotation offset from the rig's own rest pose, computed against the real bone orientations
    /// read off the prefab, so no assumption is made about which way any bone's local axes point.
    ///
    /// Rotation only, deliberately. The rig's bone translations are in hundredths of a unit and
    /// the model carries a 0.45 scale on its wrapper; a position or scale curve authored against
    /// the wrong one of those is how this project previously ended up with 400-metre ninjas. A
    /// rotation is the same rotation at any scale.
    ///
    /// Legs are left to the existing locomotion clips: the layer is masked to the spine, arms and
    /// head, so a ninja can swing while running without the two fighting over the same bones.
    /// </summary>
    public static class UnseenAnimationSetup
    {
        private const string PrefabPath = "Assets/Unseen/Art/Characters/NinjaVisual.prefab";
        private const string ClipDir = "Assets/Unseen/Art/Characters/Clips";
        private const string MaskPath = "Assets/Unseen/Art/Characters/UpperBody.mask";
        private const string ControllerPath = "Assets/Unseen/Art/Characters/NinjaAnimator.controller";

        // Bone paths, measured with Unseen > Art > Dump Ninja Rig.
        private const string Hips = "HipsCtrl/Hips";
        private const string Spine = "HipsCtrl/Hips/Spine";
        private const string Chest = "HipsCtrl/Hips/Spine/Chest";
        private const string UpperChest = "HipsCtrl/Hips/Spine/Chest/UpperChest";
        private const string Neck = UpperChest + "/Neck";
        private const string Head = Neck + "/Head";
        private const string LeftArm = UpperChest + "/LeftShoulder/LeftArm";
        private const string LeftForeArm = LeftArm + "/LeftForeArm";
        private const string LeftHand = LeftForeArm + "/LeftHand";
        private const string RightArm = UpperChest + "/RightShoulder/RightArm";
        private const string RightForeArm = RightArm + "/RightForeArm";
        private const string RightHand = RightForeArm + "/RightHand";
        private const string LeftUpLeg = "HipsCtrl/Hips/LeftUpLeg";
        private const string LeftLeg = LeftUpLeg + "/LeftLeg";
        private const string LeftFoot = LeftLeg + "/LeftFoot";
        private const string RightUpLeg = "HipsCtrl/Hips/RightUpLeg";
        private const string RightLeg = RightUpLeg + "/RightLeg";
        private const string RightFoot = RightLeg + "/RightFoot";

        /// <summary>One bone at one moment: a rotation offset from rest, about a character axis.</summary>
        private struct Pose
        {
            public string Bone;
            public Vector3 Axis;
            public float Degrees;

            public Pose(string bone, Vector3 axis, float degrees)
            {
                Bone = bone;
                Axis = axis;
                Degrees = degrees;
            }
        }

        private struct Key
        {
            public float Time;
            public Pose[] Poses;

            public Key(float time, params Pose[] poses)
            {
                Time = time;
                Poses = poses;
            }
        }

        // Character-relative axes. The prefab is instantiated unrotated, so these are world axes
        // and each bone's local equivalent is derived from its actual rest orientation.
        private static readonly Vector3 Right = Vector3.right;   // pitch: raise/lower, lean fore/aft
        private static readonly Vector3 Up = Vector3.up;         // yaw: twist, swing across the body
        private static readonly Vector3 Fwd = Vector3.forward;   // roll: tilt sideways

        [MenuItem("Unseen/Art/Build Combat Animation", priority = 57)]
        public static void Build()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[anim] no prefab at {PrefabPath}");
                return;
            }

            GameObject instance = Object.Instantiate(prefab);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;

            try
            {
                var animator = instance.GetComponentInChildren<Animator>();
                if (animator == null)
                {
                    Debug.LogError("[anim] prefab has no Animator");
                    return;
                }

                Transform root = animator.transform;
                Directory.CreateDirectory(ClipDir);

                // Pose the rig to the idle stance before reading any rest rotation.
                //
                // The bind pose is a T-pose, where both arms point straight along the X axis - so
                // "rotate the arm forty degrees about the character's right axis" spins the arm
                // about its own length and does precisely nothing. Every pose below is authored
                // against a standing figure with its arms down, which is what makes an offset like
                // "raise the forearm" mean what it says, and it also means the combat layer blends
                // out of idle instead of snapping to a T-pose.
                animator.enabled = false;
                foreach (SkinnedMeshRenderer skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    skin.forceMatrixRecalculationPerRender = true;

                var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{ClipDir}/idle.anim");
                if (idle != null)
                {
                    idle.SampleAnimation(animator.gameObject, 0f);
                    Debug.Log("[anim] rest pose taken from idle.anim at t=0");
                }
                else
                {
                    Debug.LogWarning("[anim] no idle.anim; poses will be authored against the " +
                                     "bind T-pose and arm rotations will not read correctly");
                }

                var built = new List<AnimationClip>();
                built.Add(Write(root, "ninja_guard", Guard(), loop: true));
                built.Add(Write(root, "ninja_attack_light", AttackLight(), loop: false));
                built.Add(Write(root, "ninja_attack_heavy", AttackHeavy(), loop: false));
                built.Add(Write(root, "ninja_stagger", Stagger(), loop: false));
                built.Add(Write(root, "ninja_takedown_attacker", TakedownAttacker(), loop: false));
                built.Add(Write(root, "ninja_takedown_victim", TakedownVictim(), loop: false));
                AnimationClip crouch = Write(root, "ninja_crouch", Crouch(), loop: true);
                AnimationClip prone = Write(root, "ninja_prone", Prone(), loop: true);
                AnimationClip climb = Write(root, "ninja_climb", Climb(), loop: true);
                AnimationClip wallRun = Write(root, "ninja_wallrun", WallRun(), loop: true);
                AnimationClip hang = Write(root, "ninja_hang", Hang(), loop: true);

                AvatarMask mask = BuildMask(root);
                AvatarMask stanceMask = BuildStanceMask(root);
                WireController(built, mask);
                WireStanceLayer(crouch, prone, stanceMask);
                WireParkourLayer(climb, wallRun, hang);

                built.Add(crouch);
                built.Add(prone);
                built.Add(climb);
                built.Add(wallRun);
                built.Add(hang);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                var names = new List<string>();
                foreach (AnimationClip c in built) names.Add($"{c.name} ({c.length:0.00}s)");
                Debug.Log($"[anim] built {built.Count} clips: {string.Join(", ", names)}");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // ---------------------------------------------------------------- poses

        /// <summary>Hands up, weight back, chin tucked. Held while the guard is raised.</summary>
        private static Key[] Guard()
        {
            Pose[] up =
            {
                new Pose(Spine, Right, -6f),
                new Pose(Chest, Right, -4f),
                new Pose(Head, Right, 6f),
                new Pose(RightArm, Right, -62f),
                new Pose(RightArm, Up, -18f),
                new Pose(RightForeArm, Right, -78f),
                new Pose(LeftArm, Right, -52f),
                new Pose(LeftArm, Up, 22f),
                new Pose(LeftForeArm, Right, -84f)
            };

            // Two keys with a tiny difference, so a held guard breathes instead of freezing solid.
            Pose[] settle =
            {
                new Pose(Spine, Right, -8f),
                new Pose(Chest, Right, -3f),
                new Pose(Head, Right, 5f),
                new Pose(RightArm, Right, -58f),
                new Pose(RightArm, Up, -18f),
                new Pose(RightForeArm, Right, -74f),
                new Pose(LeftArm, Right, -55f),
                new Pose(LeftArm, Up, 22f),
                new Pose(LeftForeArm, Right, -80f)
            };

            return new[] { new Key(0f, up), new Key(0.9f, settle), new Key(1.8f, up) };
        }

        /// <summary>A fast diagonal cut: coil, snap through, recover.</summary>
        private static Key[] AttackLight()
        {
            return new[]
            {
                new Key(0f),
                new Key(0.14f,
                    new Pose(Spine, Up, 26f),
                    new Pose(Chest, Up, 14f),
                    new Pose(RightArm, Up, 54f),
                    new Pose(RightArm, Right, -40f),
                    new Pose(RightForeArm, Right, -96f),
                    new Pose(LeftArm, Right, -28f),
                    new Pose(Head, Up, 18f)),
                new Key(0.26f,
                    new Pose(Spine, Up, -30f),
                    new Pose(Chest, Up, -16f),
                    new Pose(RightArm, Up, -44f),
                    new Pose(RightArm, Right, -74f),
                    new Pose(RightForeArm, Right, -14f),
                    new Pose(LeftArm, Right, -18f),
                    new Pose(LeftArm, Up, -30f),
                    new Pose(Head, Up, -14f)),
                new Key(0.40f,
                    new Pose(Spine, Up, -8f),
                    new Pose(RightArm, Right, -30f),
                    new Pose(RightForeArm, Right, -40f)),
                new Key(0.55f)
            };
        }

        /// <summary>A committed overhead: slower, deeper, and it leaves you open afterwards.</summary>
        private static Key[] AttackHeavy()
        {
            return new[]
            {
                new Key(0f),
                new Key(0.26f,
                    new Pose(Spine, Up, 34f),
                    new Pose(Spine, Right, 14f),
                    new Pose(Chest, Up, 18f),
                    new Pose(RightArm, Up, 62f),
                    new Pose(RightArm, Right, -128f),
                    new Pose(RightForeArm, Right, -70f),
                    new Pose(LeftArm, Right, -46f),
                    new Pose(LeftArm, Up, 30f),
                    new Pose(Head, Right, -12f)),
                new Key(0.44f,
                    new Pose(Spine, Up, -22f),
                    new Pose(Spine, Right, -26f),
                    new Pose(Chest, Up, -12f),
                    new Pose(Chest, Right, -14f),
                    new Pose(RightArm, Up, -20f),
                    new Pose(RightArm, Right, -34f),
                    new Pose(RightForeArm, Right, -8f),
                    new Pose(LeftArm, Right, -34f),
                    new Pose(LeftArm, Up, -34f),
                    new Pose(Head, Right, 16f)),
                new Key(0.70f,
                    new Pose(Spine, Right, -14f),
                    new Pose(RightArm, Right, -18f),
                    new Pose(RightForeArm, Right, -30f)),
                new Key(0.95f)
            };
        }

        /// <summary>Hit and rocked back. Short, so it reads without stealing control for long.</summary>
        private static Key[] Stagger()
        {
            return new[]
            {
                new Key(0f),
                new Key(0.09f,
                    new Pose(Spine, Right, 24f),
                    new Pose(Chest, Right, 14f),
                    new Pose(Head, Right, -26f),
                    new Pose(RightArm, Up, 34f),
                    new Pose(RightArm, Right, 18f),
                    new Pose(LeftArm, Up, -38f),
                    new Pose(LeftArm, Right, 16f),
                    new Pose(RightForeArm, Right, -46f),
                    new Pose(LeftForeArm, Right, -52f)),
                new Key(0.26f,
                    new Pose(Spine, Right, 8f),
                    new Pose(Head, Right, -8f),
                    new Pose(RightArm, Up, 12f),
                    new Pose(LeftArm, Up, -14f),
                    new Pose(RightForeArm, Right, -24f),
                    new Pose(LeftForeArm, Right, -28f)),
                new Key(0.45f)
            };
        }

        /// <summary>
        /// The attacker's half of the lockstep: reach, clamp, wrench down and hold.
        /// Timed to <see cref="Core.UnseenConfig.CombatSection.TakedownDuration"/>, 1.5 s.
        /// </summary>
        private static Key[] TakedownAttacker()
        {
            return new[]
            {
                new Key(0f),
                new Key(0.22f,
                    new Pose(Spine, Right, -18f),
                    new Pose(RightArm, Right, -96f),
                    new Pose(RightForeArm, Right, -34f),
                    new Pose(LeftArm, Right, -88f),
                    new Pose(LeftForeArm, Right, -40f),
                    new Pose(Head, Right, -10f)),
                new Key(0.45f,
                    new Pose(Spine, Right, -10f),
                    new Pose(Spine, Up, -14f),
                    new Pose(RightArm, Right, -72f),
                    new Pose(RightArm, Up, -30f),
                    new Pose(RightForeArm, Right, -86f),
                    new Pose(LeftArm, Right, -66f),
                    new Pose(LeftForeArm, Right, -78f)),
                new Key(0.95f,
                    new Pose(Spine, Right, 16f),
                    new Pose(Spine, Up, -26f),
                    new Pose(Chest, Right, 10f),
                    new Pose(RightArm, Right, -34f),
                    new Pose(RightArm, Up, -44f),
                    new Pose(RightForeArm, Right, -104f),
                    new Pose(LeftArm, Right, -30f),
                    new Pose(LeftForeArm, Right, -96f),
                    new Pose(Head, Right, 14f)),
                new Key(1.30f,
                    new Pose(Spine, Right, 6f),
                    new Pose(RightArm, Right, -20f),
                    new Pose(RightForeArm, Right, -40f),
                    new Pose(LeftArm, Right, -16f),
                    new Pose(LeftForeArm, Right, -36f)),
                new Key(1.50f)
            };
        }

        /// <summary>The victim's half: caught from behind, arched, then limp.</summary>
        private static Key[] TakedownVictim()
        {
            return new[]
            {
                new Key(0f),
                new Key(0.30f,
                    new Pose(Head, Right, 30f),
                    new Pose(Spine, Right, 16f),
                    new Pose(RightArm, Up, 44f),
                    new Pose(RightArm, Right, 24f),
                    new Pose(LeftArm, Up, -48f),
                    new Pose(LeftArm, Right, 20f)),
                new Key(0.70f,
                    new Pose(Head, Right, 22f),
                    new Pose(Spine, Right, 24f),
                    new Pose(Chest, Right, 12f),
                    new Pose(RightArm, Up, 20f),
                    new Pose(RightForeArm, Right, -30f),
                    new Pose(LeftArm, Up, -24f),
                    new Pose(LeftForeArm, Right, -34f)),
                new Key(1.20f,
                    new Pose(Head, Right, -34f),
                    new Pose(Spine, Right, -30f),
                    new Pose(Chest, Right, -18f),
                    new Pose(RightArm, Right, -12f),
                    new Pose(RightForeArm, Right, -18f),
                    new Pose(LeftArm, Right, -10f),
                    new Pose(LeftForeArm, Right, -16f)),
                new Key(1.50f,
                    new Pose(Head, Right, -40f),
                    new Pose(Spine, Right, -34f))
            };
        }

        /// <summary>
        /// A crouch: knees and hips folded, weight forward, head down.
        ///
        /// Rotation only, like everything else here, which means the bent knees lift the feet off
        /// the floor - AgentVisual lowers the whole body by the same amount to put them back. Not
        /// as good as a footIK pass, but it reads correctly and cannot break on an import setting.
        /// </summary>
        private static Key[] Crouch()
        {
            // Folded much deeper than the first pass, which dropped the body by 0.24 m against a
            // capsule that shrinks by 0.65 m: the ninja barely dipped while the collider halved.
            // The knee angle is what sets that drop, and AgentVisual sinks the body by whatever
            // the fold actually lifts the foot - measured, not guessed.
            Pose[] down =
            {
                new Pose(LeftUpLeg, Right, -92f),
                new Pose(LeftLeg, Right, 126f),
                new Pose(LeftFoot, Right, -38f),
                new Pose(RightUpLeg, Right, -92f),
                new Pose(RightLeg, Right, 126f),
                new Pose(RightFoot, Right, -38f),
                new Pose(Hips, Right, 26f),
                new Pose(Spine, Right, -22f),
                new Pose(Chest, Right, -12f),
                new Pose(Head, Right, 16f)
            };

            // Two near-identical keys so a held crouch has a breath in it.
            Pose[] settle =
            {
                new Pose(LeftUpLeg, Right, -89f),
                new Pose(LeftLeg, Right, 123f),
                new Pose(LeftFoot, Right, -36f),
                new Pose(RightUpLeg, Right, -89f),
                new Pose(RightLeg, Right, 123f),
                new Pose(RightFoot, Right, -36f),
                new Pose(Hips, Right, 24f),
                new Pose(Spine, Right, -20f),
                new Pose(Chest, Right, -11f),
                new Pose(Head, Right, 15f)
            };

            return new[] { new Key(0f, down), new Key(1.1f, settle), new Key(2.2f, down) };
        }

        /// <summary>
        /// Flat to the boards: hips rolled forward, legs trailing, chest low, head up to see.
        ///
        /// Prone had a height, a speed and a stealth bonus in config and no way to reach it - no
        /// key, no pose, nothing. This is the pose half.
        /// </summary>
        private static Key[] Prone()
        {
            Pose[] flat =
            {
                new Pose(Hips, Right, 84f),
                new Pose(Spine, Right, -14f),
                new Pose(Chest, Right, -10f),
                new Pose(Head, Right, -34f),
                new Pose(LeftUpLeg, Right, -8f),
                new Pose(LeftLeg, Right, 18f),
                new Pose(RightUpLeg, Right, -6f),
                new Pose(RightLeg, Right, 24f),
                new Pose(LeftArm, Right, -74f),
                new Pose(LeftArm, Up, 26f),
                new Pose(LeftForeArm, Right, -52f),
                new Pose(RightArm, Right, -70f),
                new Pose(RightArm, Up, -24f),
                new Pose(RightForeArm, Right, -56f)
            };

            Pose[] breathe =
            {
                new Pose(Hips, Right, 83f),
                new Pose(Spine, Right, -12f),
                new Pose(Chest, Right, -9f),
                new Pose(Head, Right, -32f),
                new Pose(LeftUpLeg, Right, -7f),
                new Pose(LeftLeg, Right, 20f),
                new Pose(RightUpLeg, Right, -5f),
                new Pose(RightLeg, Right, 22f),
                new Pose(LeftArm, Right, -72f),
                new Pose(LeftArm, Up, 26f),
                new Pose(LeftForeArm, Right, -50f),
                new Pose(RightArm, Right, -68f),
                new Pose(RightArm, Up, -24f),
                new Pose(RightForeArm, Right, -54f)
            };

            return new[] { new Key(0f, flat), new Key(1.4f, breathe), new Key(2.8f, flat) };
        }

        /// <summary>Hauling up a wall: alternating reaches, knees driving into the face.</summary>
        private static Key[] Climb()
        {
            Pose[] left =
            {
                new Pose(Spine, Right, -18f),
                new Pose(Head, Right, -22f),
                new Pose(LeftArm, Right, -158f),
                new Pose(LeftForeArm, Right, -16f),
                new Pose(RightArm, Right, -74f),
                new Pose(RightForeArm, Right, -66f),
                new Pose(LeftUpLeg, Right, -18f),
                new Pose(RightUpLeg, Right, -72f),
                new Pose(RightLeg, Right, 88f)
            };

            Pose[] right =
            {
                new Pose(Spine, Right, -18f),
                new Pose(Head, Right, -22f),
                new Pose(RightArm, Right, -158f),
                new Pose(RightForeArm, Right, -16f),
                new Pose(LeftArm, Right, -74f),
                new Pose(LeftForeArm, Right, -66f),
                new Pose(RightUpLeg, Right, -18f),
                new Pose(LeftUpLeg, Right, -72f),
                new Pose(LeftLeg, Right, 88f)
            };

            return new[] { new Key(0f, left), new Key(0.5f, right), new Key(1f, left) };
        }

        /// <summary>Running along a wall: body canted into it, stride wide, arms driving.</summary>
        private static Key[] WallRun()
        {
            Pose[] a =
            {
                new Pose(Hips, Fwd, 24f),
                new Pose(Spine, Fwd, 14f),
                new Pose(Spine, Right, -12f),
                new Pose(Head, Fwd, -18f),
                new Pose(LeftUpLeg, Right, -74f),
                new Pose(LeftLeg, Right, 52f),
                new Pose(RightUpLeg, Right, 26f),
                new Pose(RightLeg, Right, 34f),
                new Pose(LeftArm, Right, -52f),
                new Pose(RightArm, Right, 34f)
            };

            Pose[] b =
            {
                new Pose(Hips, Fwd, 24f),
                new Pose(Spine, Fwd, 14f),
                new Pose(Spine, Right, -12f),
                new Pose(Head, Fwd, -18f),
                new Pose(RightUpLeg, Right, -74f),
                new Pose(RightLeg, Right, 52f),
                new Pose(LeftUpLeg, Right, 26f),
                new Pose(LeftLeg, Right, 34f),
                new Pose(RightArm, Right, -52f),
                new Pose(LeftArm, Right, 34f)
            };

            return new[] { new Key(0f, a), new Key(0.32f, b), new Key(0.64f, a) };
        }

        /// <summary>Hanging: both hands above, body long, legs loose. Rafters and the rope.</summary>
        private static Key[] Hang()
        {
            Pose[] hold =
            {
                new Pose(LeftArm, Right, -168f),
                new Pose(LeftArm, Up, 12f),
                new Pose(LeftForeArm, Right, -10f),
                new Pose(RightArm, Right, -168f),
                new Pose(RightArm, Up, -12f),
                new Pose(RightForeArm, Right, -10f),
                new Pose(Spine, Right, -6f),
                new Pose(Head, Right, -14f),
                new Pose(LeftUpLeg, Right, -16f),
                new Pose(LeftLeg, Right, 30f),
                new Pose(RightUpLeg, Right, -8f),
                new Pose(RightLeg, Right, 22f)
            };

            Pose[] sway =
            {
                new Pose(LeftArm, Right, -166f),
                new Pose(LeftArm, Up, 12f),
                new Pose(LeftForeArm, Right, -12f),
                new Pose(RightArm, Right, -166f),
                new Pose(RightArm, Up, -12f),
                new Pose(RightForeArm, Right, -12f),
                new Pose(Spine, Right, -4f),
                new Pose(Head, Right, -12f),
                new Pose(LeftUpLeg, Right, -10f),
                new Pose(LeftLeg, Right, 24f),
                new Pose(RightUpLeg, Right, -14f),
                new Pose(RightLeg, Right, 28f)
            };

            return new[] { new Key(0f, hold), new Key(0.9f, sway), new Key(1.8f, hold) };
        }

        // ---------------------------------------------------------------- clip writing

        /// <summary>
        /// Turns keys into quaternion curves on real bone paths.
        ///
        /// Quaternion rather than euler: these poses swing a shoulder through more than ninety
        /// degrees, and euler curves pick a different route through gimbal space than the one you
        /// authored. Each bone's rest rotation is read off the instantiated prefab, so an offset of
        /// "forty degrees about the character's right axis" means that regardless of how the
        /// exporter happened to orient the joint.
        /// </summary>
        private static AnimationClip Write(Transform root, string name, Key[] keys, bool loop)
        {
            // Which bones this clip touches at all.
            var bones = new List<string>();
            foreach (Key key in keys)
            {
                if (key.Poses == null) continue;
                foreach (Pose pose in key.Poses)
                    if (!bones.Contains(pose.Bone))
                        bones.Add(pose.Bone);
            }

            string path = $"{ClipDir}/{name}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                clip = new AnimationClip { name = name };
                AssetDatabase.CreateAsset(clip, path);
            }
            else
            {
                clip.ClearCurves();
            }

            clip.frameRate = 30f;

            foreach (string bone in bones)
            {
                Transform joint = root.Find(bone);
                if (joint == null)
                {
                    Debug.LogWarning($"[anim] {name}: bone '{bone}' not found; skipped");
                    continue;
                }

                Quaternion rest = joint.localRotation;

                var cx = new AnimationCurve();
                var cy = new AnimationCurve();
                var cz = new AnimationCurve();
                var cw = new AnimationCurve();

                foreach (Key key in keys)
                {
                    Quaternion offset = Quaternion.identity;

                    if (key.Poses != null)
                    {
                        foreach (Pose pose in key.Poses)
                        {
                            if (pose.Bone != bone) continue;

                            // World axis expressed in this joint's own space, so the authored
                            // intent survives whatever orientation the joint rests at.
                            Vector3 localAxis = Quaternion.Inverse(joint.rotation) * pose.Axis;
                            offset = Quaternion.AngleAxis(pose.Degrees, localAxis) * offset;
                        }
                    }

                    Quaternion target = rest * offset;

                    cx.AddKey(key.Time, target.x);
                    cy.AddKey(key.Time, target.y);
                    cz.AddKey(key.Time, target.z);
                    cw.AddKey(key.Time, target.w);
                }

                Smooth(cx);
                Smooth(cy);
                Smooth(cz);
                Smooth(cw);

                clip.SetCurve(bone, typeof(Transform), "localRotation.x", cx);
                clip.SetCurve(bone, typeof(Transform), "localRotation.y", cy);
                clip.SetCurve(bone, typeof(Transform), "localRotation.z", cz);
                clip.SetCurve(bone, typeof(Transform), "localRotation.w", cw);
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            EditorUtility.SetDirty(clip);
            return clip;
        }

        private static void Smooth(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0.35f);
        }

        // ---------------------------------------------------------------- mask and controller

        /// <summary>
        /// Upper body only: spine up, both arms, head. Everything else is left to the locomotion
        /// layer, so a ninja can cut while sprinting and the legs keep running.
        /// </summary>
        private static AvatarMask BuildMask(Transform root)
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
            if (mask == null)
            {
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, MaskPath);
            }

            var all = new List<Transform>();
            Collect(root, root, all);

            mask.transformCount = all.Count;
            int active = 0;

            for (int i = 0; i < all.Count; i++)
            {
                string path = Relative(all[i], root);
                bool include = path.Length == 0 ||
                               path.StartsWith(Spine) ||
                               path == Hips ||
                               path == "HipsCtrl";

                mask.SetTransformPath(i, path);
                mask.SetTransformActive(i, include);
                if (include) active++;
            }

            EditorUtility.SetDirty(mask);
            Debug.Log($"[anim] upper-body mask: {active}/{all.Count} transforms active");
            return mask;
        }

        private static void Collect(Transform node, Transform root, List<Transform> into)
        {
            into.Add(node);
            for (int i = 0; i < node.childCount; i++) Collect(node.GetChild(i), root, into);
        }

        private static string Relative(Transform node, Transform root)
        {
            if (node == root) return string.Empty;

            var parts = new List<string>();
            Transform cursor = node;
            while (cursor != null && cursor != root)
            {
                parts.Add(cursor.name);
                cursor = cursor.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>Hips and legs, for the stance layer. Arms are left to combat and locomotion.</summary>
        private static AvatarMask BuildStanceMask(Transform root)
        {
            const string path = "Assets/Unseen/Art/Characters/Stance.mask";
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
            if (mask == null)
            {
                mask = new AvatarMask();
                AssetDatabase.CreateAsset(mask, path);
            }

            var all = new List<Transform>();
            Collect(root, root, all);

            mask.transformCount = all.Count;
            int active = 0;

            for (int i = 0; i < all.Count; i++)
            {
                string bone = Relative(all[i], root);

                // Hips, both legs, and the spine chain up to the chest - but not the arms, which
                // the combat layer owns, and not the head, which would fight the guard pose.
                bool include = bone.Length == 0 ||
                               bone == "HipsCtrl" ||
                               bone == Hips ||
                               bone.StartsWith(LeftUpLeg) ||
                               bone.StartsWith(RightUpLeg) ||
                               bone == Spine ||
                               bone == Chest;

                mask.SetTransformPath(i, bone);
                mask.SetTransformActive(i, include);
                if (include) active++;
            }

            EditorUtility.SetDirty(mask);
            Debug.Log($"[anim] stance mask: {active}/{all.Count} transforms active");
            return mask;
        }

        /// <summary>
        /// The stance layer: crouch and prone, chosen by an integer, weight driven from code.
        /// </summary>
        private static void WireStanceLayer(AnimationClip crouch, AnimationClip prone, AvatarMask mask)
        {
            AnimatorControllerLayer layer = AddLayer("Stance", mask, out AnimatorController controller);
            if (layer == null) return;

            const string parameter = "Stance";
            EnsureIntParameter(controller, parameter);

            AnimatorState idle = layer.stateMachine.AddState("Upright");
            layer.stateMachine.defaultState = idle;

            AddDrivenState(layer.stateMachine, idle, crouch, "Crouch", parameter, 1);
            AddDrivenState(layer.stateMachine, idle, prone, "Prone", parameter, 2);

            EditorUtility.SetDirty(controller);
            Debug.Log($"[anim] stance layer: crouch and prone; controller has {controller.layers.Length} layers");
        }

        /// <summary>
        /// The parkour layer: climbing, wall running and hanging.
        ///
        /// Full body and unmasked, because none of these are things the legs and the arms can
        /// disagree about - a ninja halfway up a wall is not also idling from the waist down.
        /// </summary>
        private static void WireParkourLayer(AnimationClip climb, AnimationClip wallRun, AnimationClip hang)
        {
            AnimatorControllerLayer layer = AddLayer("Parkour", null, out AnimatorController controller);
            if (layer == null) return;

            const string parameter = "Parkour";
            EnsureIntParameter(controller, parameter);

            AnimatorState idle = layer.stateMachine.AddState("Grounded");
            layer.stateMachine.defaultState = idle;

            AddDrivenState(layer.stateMachine, idle, climb, "Climb", parameter, 1);
            AddDrivenState(layer.stateMachine, idle, wallRun, "WallRun", parameter, 2);
            AddDrivenState(layer.stateMachine, idle, hang, "Hang", parameter, 3);

            EditorUtility.SetDirty(controller);
            Debug.Log($"[anim] parkour layer: climb, wall run and hang; " +
                      $"controller has {controller.layers.Length} layers");
        }

        /// <summary>Removes any layer of this name and adds a fresh one, so the tool is idempotent.</summary>
        private static AnimatorControllerLayer AddLayer(string name, AvatarMask mask,
            out AnimatorController controller)
        {
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null) return null;

            for (int i = controller.layers.Length - 1; i >= 1; i--)
                if (controller.layers[i].name == name)
                    controller.RemoveLayer(i);

            controller.AddLayer(name);
            AnimatorControllerLayer[] layers = controller.layers;
            AnimatorControllerLayer layer = layers[layers.Length - 1];

            layer.avatarMask = mask;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.defaultWeight = 0f; // raised from code, never left on
            layers[layers.Length - 1] = layer;
            controller.layers = layers;
            return layer;
        }

        private static void EnsureIntParameter(AnimatorController controller, string parameter)
        {
            foreach (AnimatorControllerParameter p in controller.parameters)
                if (p.name == parameter)
                    return;

            controller.AddParameter(parameter, AnimatorControllerParameterType.Int);
        }

        private static void AddDrivenState(AnimatorStateMachine machine, AnimatorState idle,
            AnimationClip clip, string name, string parameter, int value)
        {
            if (clip == null) return;

            AnimatorState state = machine.AddState(name);
            state.motion = clip;

            AnimatorStateTransition enter = machine.AddAnyStateTransition(state);
            enter.AddCondition(AnimatorConditionMode.Equals, value, parameter);
            enter.duration = 0.12f;
            enter.hasExitTime = false;
            enter.canTransitionToSelf = false;

            AnimatorStateTransition leave = state.AddTransition(idle);
            leave.AddCondition(AnimatorConditionMode.NotEqual, value, parameter);
            leave.duration = 0.15f;
            leave.hasExitTime = false;
        }

        /// <summary>
        /// Adds the combat layer.
        ///
        /// The layer is driven by one integer and its weight is controlled from code rather than by
        /// an empty pass-through state: an empty state on an override layer writes the rig's
        /// default pose over the base layer, which reads as the ninja snapping to a T-pose between
        /// swings. Weight zero is unambiguous.
        /// </summary>
        private static void WireController(List<AnimationClip> clips, AvatarMask mask)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError($"[anim] no controller at {ControllerPath}; run Build Ninja Character first");
                return;
            }

            const string parameter = "Action";
            bool hasParameter = false;
            foreach (AnimatorControllerParameter p in controller.parameters)
                if (p.name == parameter)
                    hasParameter = true;

            if (!hasParameter) controller.AddParameter(parameter, AnimatorControllerParameterType.Int);

            // Rebuild the layer from scratch each run, so this tool is idempotent.
            const string layerName = "Combat";
            for (int i = controller.layers.Length - 1; i >= 1; i--)
                if (controller.layers[i].name == layerName)
                    controller.RemoveLayer(i);

            controller.AddLayer(layerName);
            AnimatorControllerLayer[] layers = controller.layers;
            AnimatorControllerLayer layer = layers[layers.Length - 1];

            layer.avatarMask = mask;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.defaultWeight = 0f; // raised from code while an action is playing
            layers[layers.Length - 1] = layer;
            controller.layers = layers;

            AnimatorStateMachine machine = layer.stateMachine;

            // Action ids, matched by AgentVisual.
            var actions = new (int Id, string Clip)[]
            {
                (1, "ninja_guard"),
                (2, "ninja_attack_light"),
                (3, "ninja_attack_heavy"),
                (4, "ninja_stagger"),
                (5, "ninja_takedown_attacker"),
                (6, "ninja_takedown_victim")
            };

            AnimatorState idle = machine.AddState("None");
            machine.defaultState = idle;

            foreach ((int id, string clipName) in actions)
            {
                AnimationClip clip = clips.Find(c => c != null && c.name == clipName);
                if (clip == null) continue;

                AnimatorState state = machine.AddState(clipName);
                state.motion = clip;

                AnimatorStateTransition enter = machine.AddAnyStateTransition(state);
                enter.AddCondition(AnimatorConditionMode.Equals, id, parameter);
                enter.duration = 0.08f;
                enter.hasExitTime = false;
                enter.canTransitionToSelf = false;

                AnimatorStateTransition leave = state.AddTransition(idle);
                leave.AddCondition(AnimatorConditionMode.NotEqual, id, parameter);
                leave.duration = 0.12f;
                leave.hasExitTime = false;
            }

            EditorUtility.SetDirty(controller);
            Debug.Log($"[anim] controller: {controller.layers.Length} layers, " +
                      $"{controller.parameters.Length} parameters, {actions.Length} actions");
        }
    }
}
