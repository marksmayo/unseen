using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Video;

namespace Unseen.Client
{
    /// <summary>Plays the authored eight-second Blender film once per application session.</summary>
    public sealed class FirstLoadIntro : MonoBehaviour
    {
        private static bool _shown;
        private VideoPlayer _player;
        private RenderTexture _target;
        private VideoClip _clip;
        private Camera _backdrop;
        private bool _failed;
        private bool _finishing;
        private bool _skipRequested;
        private float _startedAt;
        private CursorLockMode _oldLock;
        private bool _oldVisible;
        private GUIStyle _hint;
        private Action _complete;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession() => _shown = false;

        public static bool ShouldPlay(bool dedicated)
        {
            if (_shown || dedicated || Application.isBatchMode || !Application.isPlaying) return false;
            foreach (string arg in System.Environment.GetCommandLineArgs())
                if (arg == "-server" || arg == "--server" || arg == "-skipintro") return false;
            return true;
        }

        public void Play(Action complete)
        {
            _shown = true;
            _complete = complete;
            _startedAt = Time.realtimeSinceStartup;
            _oldLock = Cursor.lockState;
            _oldVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = false;
            _backdrop = gameObject.AddComponent<Camera>();
            _backdrop.clearFlags = CameraClearFlags.SolidColor;
            _backdrop.backgroundColor = Color.black;
            _backdrop.cullingMask = 0;
            _backdrop.depth = 100;
            StartCoroutine(Playback());
        }

        private IEnumerator Playback()
        {
            ResourceRequest request = Resources.LoadAsync<VideoClip>("Intro/UnseenIntro");
            bool skipped = false;
            // Resource requests cannot be cancelled; remember skip while loading.
            while (!request.isDone) { skipped |= SkipPressed(); yield return null; }
            _clip = request.asset as VideoClip;
            if (skipped) { yield return Finish(); yield break; }
            if (_clip == null)
            {
                Debug.LogWarning("[Unseen] Intro film missing; continuing startup.");
                yield return Finish();
                yield break;
            }
            _target = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32);
            _target.Create();
            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.source = VideoSource.VideoClip;
            _player.clip = _clip;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _target;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.isLooping = false;
            _player.waitForFirstFrame = true;
            _player.errorReceived += OnVideoError;
            _player.Prepare();
            float deadline = Time.realtimeSinceStartup + 4f;
            while (!_player.isPrepared && !_failed && Time.realtimeSinceStartup < deadline)
            {
                if (SkipPressed()) { yield return Finish(); yield break; }
                yield return null;
            }
            if (!_player.isPrepared || _failed) { yield return Finish(); yield break; }
            _player.Play();
            // A real-time bound guarantees a broken decoder cannot trap the player here.
            deadline = Time.realtimeSinceStartup + 9f;
            while (!_failed && Time.realtimeSinceStartup < deadline)
            {
                if (SkipPressed() || _player.time >= 7.96 || (_player.frame > 0 && !_player.isPlaying)) break;
                yield return null;
            }
            yield return Finish();
        }

        /// <summary>Allows a controller or an external UI to skip the film.</summary>
        public void Skip() => _skipRequested = true;

        private bool SkipPressed() => _skipRequested || Time.realtimeSinceStartup - _startedAt > .35f &&
            (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0));

        private void OnVideoError(VideoPlayer player, string message)
        {
            _failed = true;
            Debug.LogWarning("[Unseen] Intro playback failed: " + message);
        }

        private IEnumerator Finish()
        {
            _finishing = true;
            if (_player != null) _player.Stop();
            // Present a clean black frame before the synchronous world/navmesh startup.
            yield return null;
            RestoreCursor();
            if (_backdrop != null) _backdrop.enabled = false;
            Action complete = _complete;
            _complete = null;
            complete?.Invoke();
            Destroy(gameObject);
        }

        private void OnGUI()
        {
            int previousDepth = GUI.depth;
            Color previousColor = GUI.color;
            GUI.depth = -10000;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            if (!_finishing && _player != null && _player.frame >= 0 && _target != null)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _target, ScaleMode.ScaleToFit, false);
            if (!_finishing && Time.realtimeSinceStartup - _startedAt > 1f)
            {
                if (_hint == null) _hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight };
                _hint.fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 60f), 11, 20);
                GUI.color = new Color(.75f, .8f, .83f, .7f);
                GUI.Label(new Rect(20, Screen.height - 48, Screen.width - 48, 28), "SPACE / ESC  ·  SKIP", _hint);
            }
            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }

        private void RestoreCursor()
        {
            Cursor.lockState = _oldLock;
            Cursor.visible = _oldVisible;
        }

        private void OnDestroy()
        {
            if (!_finishing) RestoreCursor();
            if (_player != null)
            {
                _player.errorReceived -= OnVideoError;
                _player.Stop();
                _player.targetTexture = null;
                _player.clip = null;
            }
            if (_target != null) { _target.Release(); Destroy(_target); }
            if (_clip != null) Resources.UnloadAsset(_clip);
        }
    }
}
