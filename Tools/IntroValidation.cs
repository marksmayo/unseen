// Copy into an isolated Unity project's Assets/Editor with FirstLoadIntro.cs and its Resources film.
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Video;
using Unseen.Client;

[InitializeOnLoad]
public static class IntroValidation
{
    static FirstLoadIntro intro;
    static double started;
    static int phase;
    static bool captured, completed, skipSent;
    static IntroValidation()
    {
        if (SessionState.GetBool("IntroValidation.Run", false)) EditorApplication.update += Tick;
    }
    public static void Run() { SessionState.SetBool("IntroValidation.Skip", false); Begin(); }
    public static void RunSkip() { SessionState.SetBool("IntroValidation.Skip", true); Begin(); }
    static void Begin()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool("IntroValidation.Run", true);
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        EditorApplication.EnterPlaymode();
    }
    static void Tick()
    {
        if (!EditorApplication.isPlaying || EditorApplication.isCompiling) return;
        try
        {
            if (phase == 0)
            {
                var clip = Resources.Load<VideoClip>("Intro/UnseenIntro");
                if (clip == null || clip.width != 1280 || clip.height != 720 || Math.Abs(clip.length - 8) > .1)
                    throw new Exception("Film metadata is incorrect or missing");
                Debug.Log("INTRO: video 1280x720, " + clip.frameCount + " frames, " + clip.length + " seconds");
                started = EditorApplication.timeSinceStartup;
                intro = new GameObject("Playback validation").AddComponent<FirstLoadIntro>();
                intro.Play(() => completed = true);
                phase = 1;
            }
            double elapsed = EditorApplication.timeSinceStartup - started;
            if (phase == 1)
            {
                var player = intro != null ? intro.GetComponent<VideoPlayer>() : null;
                if (!captured && player != null && player.frame >= 90)
                {
                    var previous = RenderTexture.active;
                    RenderTexture.active = player.targetTexture;
                    var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); image.Apply();
                    File.WriteAllBytes("playback.png", image.EncodeToPNG());
                    UnityEngine.Object.Destroy(image); RenderTexture.active = previous;
                    captured = true;
                }
                bool skipTest = SessionState.GetBool("IntroValidation.Skip", false);
                if (skipTest && !skipSent && elapsed > 1 && player != null && player.isPlaying && player.frame > 0)
                {
                    skipSent = true;
                    intro.Skip();
                }
                if (completed && intro == null)
                {
                    if (skipTest)
                    {
                        if (!skipSent || elapsed > 4) throw new Exception("Skip failed or video failed before skip");
                        Debug.Log("INTRO: skip and cleanup passed in " + elapsed);
                    }
                    else
                    {
                        if (!captured || elapsed < 7.5 || elapsed > 14) throw new Exception("Natural completion failed at " + elapsed);
                        Debug.Log("INTRO: natural completion and cleanup passed in " + elapsed);
                    }
                    if (FirstLoadIntro.ShouldPlay(true)) throw new Exception("Dedicated server should bypass intro");
                    Debug.Log("INTRO VALIDATION PASSED");
                    Finish(0);
                }
                else if (elapsed > 18) throw new Exception("Playback never completed");
            }
        }
        catch (Exception e) { Debug.LogException(e); Finish(1); }
    }
    static void Finish(int code)
    {
        SessionState.SetBool("IntroValidation.Run", false);
        EditorApplication.update -= Tick;
        EditorApplication.Exit(code);
    }
}
