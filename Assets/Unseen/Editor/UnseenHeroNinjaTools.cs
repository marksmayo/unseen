using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unseen.Entities;
namespace Unseen.EditorTools
{
    public static class UnseenHeroNinjaTools
    {
        [Serializable] public class BoneData { public string name; public int parent; public Vector3 position; public Quaternion rotation; }
        [Serializable] public class RigData { public BoneData[] bones; public float[] worldToMesh; public float visualScale; public Vector3[] originalWorld; }
        public static void ExportRig()
        {
            var set=AgentVisualSet.Load();var host=new GameObject("Hero export");
            try
            {
                var visual=set.Attach(host.transform,0);var body=visual.Body;
                var data=new RigData{bones=new BoneData[body.bones.Length],worldToMesh=new float[16],visualScale=visual.transform.localScale.x};
                var all=body.bones;
                for(int i=0;i<all.Length;i++) data.bones[i]=new BoneData{name=all[i].name,parent=Array.IndexOf(all,all[i].parent),position=all[i].position,rotation=all[i].rotation};
                var m=body.transform.worldToLocalMatrix;for(int r=0;r<4;r++)for(int c=0;c<4;c++)data.worldToMesh[r*4+c]=m[r,c];
                var mesh=new Mesh();body.BakeMesh(mesh,true);data.originalWorld=mesh.vertices;for(int i=0;i<data.originalWorld.Length;i++)data.originalWorld[i]=body.transform.TransformPoint(data.originalWorld[i]);
                UnityEngine.Object.DestroyImmediate(mesh);
                Directory.CreateDirectory("ArtSource/HeroNinja");File.WriteAllText("ArtSource/HeroNinja/rig.json",JsonUtility.ToJson(data,true));
                foreach(var b in data.bones)if(!b.name.Contains("Hand")&&!b.name.Contains("end"))Debug.Log($"[hero-rig] {b.name}: {b.position:F4}");
                Debug.Log("[hero-rig] exported "+data.bones.Length+" bones; visual scale "+data.visualScale);
            }
            finally{UnityEngine.Object.DestroyImmediate(host);}
        }
    }
}
