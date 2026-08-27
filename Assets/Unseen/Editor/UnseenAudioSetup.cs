using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unseen.Audio;
using Unseen.Core;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Imports the generated WAVs and wires them into an <see cref="AudioBank"/>.
    ///
    /// Clip files are matched by name, so replacing a placeholder with a recorded take is a matter
    /// of dropping a file with the same name into the folder and running this again.
    /// </summary>
    public static class UnseenAudioSetup
    {
        private const string AudioRoot = "Assets/Unseen/Art/Audio";
        private const string BankPath = "Assets/Unseen/Resources/AudioBank.asset";

        [MenuItem("Unseen/Art/Build Audio Bank", priority = 51)]
        public static void BuildBank()
        {
            if (!Directory.Exists(AudioRoot))
            {
                Debug.LogError($"[Unseen] no audio at {AudioRoot}. Nothing to build.");
                return;
            }

            ConfigureImporters();

            AudioBank bank = AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
            if (bank == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BankPath));
                bank = ScriptableObject.CreateInstance<AudioBank>();
                AssetDatabase.CreateAsset(bank, BankPath);
            }

            bank.FootstepSoft = Series("footstep_soft_", 4);
            bank.FootstepHard = Series("footstep_hard_", 4);
            bank.FootstepWood = Series("footstep_wood_", 3);
            bank.FootstepWater = Series("footstep_water_", 3);

            bank.WindBed = Clip("wind_bed");
            bank.WindGusts = Series("wind_gust_", 3);
            bank.FallWind = Clip("fall_wind");
            bank.RiverFlow = Clip("river_flow");

            var entries = new List<AudioBank.Entry>
            {
                // Footsteps carry a set per surface as well; this entry is the fallback and the
                // place the volume and range for a footstep are decided.
                Entry(SoundKind.Footstep, 0.55f, 0.14f, 34f, Series("footstep_hard_", 4)),
                Entry(SoundKind.Landing, 0.9f, 0.08f, 45f, Both("landing_soft", "landing_hard")),
                Entry(SoundKind.Vault, 0.5f, 0.12f, 30f, Series("footstep_wood_", 3)),
                Entry(SoundKind.GrappleFire, 0.8f, 0.08f, 40f, Both("grapple_fire")),
                Entry(SoundKind.WeaponSwing, 0.7f, 0.14f, 28f, Series("swing_", 3)),
                Entry(SoundKind.WeaponClash, 1f, 0.1f, 50f, Concat(Series("hit_flesh_", 3), Series("hit_block_", 2))),
                Entry(SoundKind.ShojiSlice, 0.8f, 0.1f, 34f, Both("shoji_slice")),
                Entry(SoundKind.ShojiBreak, 0.9f, 0.1f, 40f, Both("shoji_slice")),
                Entry(SoundKind.LanternBreak, 0.95f, 0.1f, 44f, Both("lantern_break")),
                Entry(SoundKind.Noisemaker, 1f, 0.08f, 55f, Both("noisemaker")),
                Entry(SoundKind.SmokeBomb, 1f, 0.08f, 50f, Both("smoke_bomb")),
                Entry(SoundKind.Death, 1f, 0.06f, 60f, Series("death_", 2)),
                Entry(SoundKind.LootContainer, 0.8f, 0.1f, 34f, Both("loot_container")),
                Entry(SoundKind.BambooRustle, 0.85f, 0.14f, 40f, Series("bamboo_rustle_", 3)),
                Entry(SoundKind.BirdFlush, 0.9f, 0.12f, 46f, Series("bird_flush_", 3)),
                Entry(SoundKind.AnimalScatter, 0.7f, 0.16f, 28f, Series("animal_scatter_", 3)),
                Entry(SoundKind.Choking, 0.95f, 0.1f, 32f, Series("choking_", 3))
            };

            entries.RemoveAll(e => e.Clips == null || e.Clips.Length == 0);
            bank.Entries = entries.ToArray();

            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int total = 0;
            foreach (AudioBank.Entry e in bank.Entries) total += e.Clips.Length;

            Debug.Log($"[Unseen] audio bank: {bank.Entries.Length} kinds, {total} clip slots, " +
                      $"footsteps soft/hard/wood/water = {bank.FootstepSoft.Length}/" +
                      $"{bank.FootstepHard.Length}/{bank.FootstepWood.Length}/{bank.FootstepWater.Length}, " +
                      $"wind bed={(bank.WindBed != null)} gusts={bank.WindGusts.Length} " +
                      $"fall={(bank.FallWind != null)} river={(bank.RiverFlow != null)}");
        }

        private static AudioBank.Entry Entry(SoundKind kind, float volume, float jitter,
            float maxDistance, AudioClip[] clips)
        {
            return new AudioBank.Entry
            {
                Kind = kind,
                Clips = clips,
                Volume = volume,
                PitchJitter = jitter,
                MaxDistance = maxDistance
            };
        }

        private static AudioClip Clip(string name)
        {
            return AssetDatabase.LoadAssetAtPath<AudioClip>($"{AudioRoot}/{name}.wav");
        }

        private static AudioClip[] Series(string prefix, int count)
        {
            var clips = new List<AudioClip>(count);
            for (int i = 1; i <= count; i++)
            {
                AudioClip clip = Clip($"{prefix}{i}");
                if (clip != null) clips.Add(clip);
            }

            return clips.ToArray();
        }

        private static AudioClip[] Both(params string[] names)
        {
            var clips = new List<AudioClip>(names.Length);
            foreach (string name in names)
            {
                AudioClip clip = Clip(name);
                if (clip != null) clips.Add(clip);
            }

            return clips.ToArray();
        }

        private static AudioClip[] Concat(AudioClip[] a, AudioClip[] b)
        {
            var all = new List<AudioClip>(a);
            all.AddRange(b);
            return all.ToArray();
        }

        /// <summary>
        /// Short clips decompress on load so a footstep never stalls on a disk read; the long wind
        /// bed streams, because keeping twelve seconds of it decompressed in memory is waste.
        /// </summary>
        private static void ConfigureImporters()
        {
            foreach (string path in Directory.GetFiles(AudioRoot, "*.wav"))
            {
                string asset = path.Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(asset) as AudioImporter;
                if (importer == null) continue;

                bool isBed = asset.Contains("wind_bed") || asset.Contains("fall_wind") ||
                             asset.Contains("river_flow");

                AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                settings.loadType = isBed ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = isBed ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.PCM;
                settings.quality = 0.7f;

                // Preload moved onto the per-platform sample settings in newer Unity versions;
                // the importer-level property is obsolete and no longer compiles.
                settings.preloadAudioData = !isBed;

                importer.defaultSampleSettings = settings;
                importer.forceToMono = true;
                importer.loadInBackground = isBed;
                importer.SaveAndReimport();
            }
        }
    }
}
