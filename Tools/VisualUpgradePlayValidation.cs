using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unseen.Client;
using Unseen.Core;
using Unseen.Combat;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Net;

[InitializeOnLoad]
public static class VisualUpgradePlayValidation
{
    static int phase, frame, errors;
    static double started;
    static UnseenBootstrap boot;
    static CombatFeedback feedback;
    static ThirdPersonCameraRig rig;
    static SnapshotData snapshot;
    static Vector4 wind;
    static float baselineFov;
    static GameObject wall;
    static VisualUpgradePlayValidation() { if(SessionState.GetBool("VisualUpgradePlayValidation",false)) EditorApplication.update+=Tick; }
    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        SessionState.SetBool("VisualUpgradePlayValidation",true);
        EditorApplication.update-=Tick;EditorApplication.update+=Tick;
        EditorApplication.EnterPlaymode();
    }
    static void Tick()
    {
        if(!EditorApplication.isPlaying || EditorApplication.isCompiling)return;
        try
        {
            if(phase==0)
            {
                Application.logMessageReceived+=Log;
                var host=new GameObject("Visual runtime match");host.SetActive(false);
                boot=host.AddComponent<UnseenBootstrap>();boot.BuildNavMesh=false;boot.StatusLogInterval=0;boot.VerboseStartup=false;host.SetActive(true);
                if(boot.Simulation==null)throw new Exception("Match did not boot");
                for(int i=0;i<3600;i++){boot.Network.Poll(1f/60);boot.Simulation.Advance(1f/60);}
                if(boot.Context.Entities.Count!=64)throw new Exception("Expected 64 actors");
                if(Camera.main==null)throw new Exception("Player camera not discoverable");
                int motions=UnityEngine.Object.FindObjectsByType<NinjaMotionPolish>().Length;
                if(motions<64)throw new Exception("Missing character motion components: "+motions);
                var target=new GameObject("Effect probe target");target.transform.position=new Vector3(1000,10,1000);
                rig=new GameObject("Effect probe camera").AddComponent<ThirdPersonCameraRig>();rig.Input=target.AddComponent<PlayerInputSource>();rig.Input.enabled=false;rig.SetTarget(target.transform);
                feedback=rig.gameObject.AddComponent<CombatFeedback>();feedback.CameraRig=rig;
                phase=1;frame=Time.frameCount;started=EditorApplication.timeSinceStartup;
            }
            if(EditorApplication.timeSinceStartup-started>90)throw new Exception("Runtime probe timed out");
            if(phase==1 && Time.frameCount>frame+3)
            {
                wind=Shader.GetGlobalVector("_UnseenWind");
                baselineFov=rig.GetComponent<Camera>().fieldOfView;
                var point=rig.transform.position+rig.transform.forward*3;
                snapshot=new SnapshotData{SelfId=new AgentId(1)};
                snapshot.Combat.Add(new CombatEvent{Tick=1,Attacker=new AgentId(2),Victim=new AgentId(1),Kind=CombatEventKind.Parried,Position=point});
                feedback.SendMessage("OnSnapshot",snapshot);
                if(feedback.EffectsPlayed!=1)throw new Exception("Visible self impact did not play");
                feedback.SendMessage("OnSnapshot",snapshot);
                if(feedback.EffectsPlayed!=1)throw new Exception("Repeated event played twice");
                snapshot.Combat.Clear();snapshot.Combat.Add(new CombatEvent{Tick=2,Attacker=new AgentId(2),Victim=new AgentId(3),Kind=CombatEventKind.Hit,Position=point});
                feedback.SendMessage("OnSnapshot",snapshot);
                if(feedback.EffectsPlayed!=1)throw new Exception("Hidden combat played");
                wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.layer=UnseenLayers.Occluder;
                wall.transform.position=rig.transform.position+rig.transform.forward*1.5f;wall.transform.localScale=Vector3.one;
                Physics.SyncTransforms();snapshot.Combat.Clear();snapshot.Combat.Add(new CombatEvent{Tick=3,Attacker=new AgentId(2),Victim=new AgentId(1),Kind=CombatEventKind.Hit,Position=point});
                feedback.SendMessage("OnSnapshot",snapshot);
                if(feedback.EffectsPlayed!=1)throw new Exception("Occluded combat played");
                UnityEngine.Object.Destroy(wall);
                phase=2;frame=Time.frameCount;
            }
            if(phase==2 && Time.frameCount>frame+2)
            {
                if(rig.GetComponent<Camera>().fieldOfView>=baselineFov)throw new Exception("Impact FOV did not respond");
                if(Shader.GetGlobalVector("_UnseenWind").w<=wind.w)throw new Exception("Wind clock did not advance");
                Debug.Log("[visual-runtime] PASS: 64 actors, motion components, player camera, wind clock, visible effects, deduplication, hidden/occluded rejection and impact FOV");
                phase=3;frame=Time.frameCount;
            }
            if(phase==3 && Time.frameCount>frame+45)
            {
                if(feedback.GetComponentInChildren<ParticleSystem>().particleCount!=0)throw new Exception("Impact particles did not expire");
                foreach(var line in feedback.GetComponentsInChildren<LineRenderer>())if(line.enabled)throw new Exception("Blade trail did not expire");
                if(errors>0)throw new Exception("Runtime produced "+errors+" errors");
                int heroes=0;foreach(var visual in UnityEngine.Object.FindObjectsByType<AgentVisual>())
                {
                    if(!HeroNinjaAppearance.IsHero(visual))throw new Exception("Legacy character still active");
                    var lod=visual.GetComponent<LODGroup>();if(lod==null || lod.lodCount!=3)throw new Exception("Missing hero LODs");
                    var mesh=new Mesh();visual.Body.BakeMesh(mesh,true);var size=Vector3.Scale(mesh.bounds.size,visual.Body.transform.lossyScale);
                    if(float.IsNaN(size.x)||size.magnitude>4 || size.magnitude<.1f)throw new Exception("Invalid animated hero bounds: "+size);
                    UnityEngine.Object.Destroy(mesh);heroes++;
                }
                if(heroes<64)throw new Exception("Expected 64 hero visuals: "+heroes);
                Debug.Log("[hero-runtime] PASS: "+heroes+" animated heroes, three LODs and finite skin bounds");
                Debug.Log("[visual-runtime] PASS: effect cleanup and error-free play frames");Finish(0);
            }
        }
        catch(Exception e){Debug.LogException(e);Finish(1);}
    }
    static void Log(string message,string stack,LogType type){if((type==LogType.Exception||type==LogType.Error) && !stack.Contains("UnityEditor.Search.SearchDatabase"))errors++;}
    static void Finish(int code){SessionState.SetBool("VisualUpgradePlayValidation",false);EditorApplication.update-=Tick;Application.logMessageReceived-=Log;EditorApplication.Exit(code);}
}
