using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
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
                var original=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Unseen/Art/Characters/NinjaVisual.prefab");
                var instance=UnityEngine.Object.Instantiate(original,host.transform);var visual=instance.GetComponent<AgentVisual>();NinjaBodyArt.Apply(visual);var body=visual.Body;
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

        private const string HeroDir = "Assets/Unseen/Art/Characters/Hero";
        [Serializable] private class WeightData
        {
            public int boneIndex0,boneIndex1,boneIndex2,boneIndex3;
            public float weight0,weight1,weight2,weight3;
            public BoneWeight Value => new BoneWeight { boneIndex0=boneIndex0,boneIndex1=boneIndex1,boneIndex2=boneIndex2,boneIndex3=boneIndex3,weight0=weight0,weight1=weight1,weight2=weight2,weight3=weight3 };
        }
        [Serializable] private class MeshData
        {
            public Vector3[] vertices,normals,bonePositions;
            public Vector2[] uv,surface;
            public Color[] colors;
            public WeightData[] weights;
            public int[] triangles;
            public string[] boneNames;
            public float visualScale;
        }
        private struct Rest { public Vector3 Position; public Quaternion Rotation; public Vector3 Scale; }

        [MenuItem("Unseen/Art/Build Hero Ninja", priority=57)]
        public static void Build()
        {
            var data=JsonUtility.FromJson<MeshData>(File.ReadAllText("ArtSource/HeroNinja/hero-mesh.json"));
            Directory.CreateDirectory(HeroDir+"/Clips");AssetDatabase.Refresh();
            var original=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Unseen/Art/Characters/NinjaVisual.prefab");
            var subject=UnityEngine.Object.Instantiate(original);
            try
            {
                subject.name="HeroNinja";subject.transform.localScale=Vector3.one*data.visualScale;
                var visual=subject.GetComponent<AgentVisual>();var body=visual.Body;var animator=visual.Rig;
                if(animator==null)animator=subject.GetComponentInChildren<Animator>();
                animator.enabled=false;
                var originalRest=new Dictionary<string,Rest>();
                foreach(var t in animator.GetComponentsInChildren<Transform>(true))
                    originalRest[AnimationUtility.CalculateTransformPath(t,animator.transform)]=new Rest{Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale};
                if(body.bones.Length!=data.boneNames.Length)throw new Exception("Source rig changed; re-export it");
                for(int i=0;i<body.bones.Length;i++)
                {
                    if(body.bones[i].name!=data.boneNames[i])throw new Exception("Bone order mismatch");
                    body.bones[i].position=data.bonePositions[i];
                }
                var heroRest=new Dictionary<string,Rest>();
                foreach(var t in animator.GetComponentsInChildren<Transform>(true))
                    heroRest[AnimationUtility.CalculateTransformPath(t,animator.transform)]=new Rest{Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale};
                Matrix4x4[] bind=new Matrix4x4[body.bones.Length];
                for(int i=0;i<bind.Length;i++)bind[i]=body.bones[i].worldToLocalMatrix*body.transform.localToWorldMatrix;
                body.sharedMesh=MakeMesh(data,"HeroNinja_LOD0",bind);
                var lodData=JsonUtility.FromJson<MeshData>(File.ReadAllText("ArtSource/HeroNinja/hero-lod1.json"));
                var lowMesh=MakeMesh(lodData,"HeroNinja_LOD1",bind);
                var distantMesh=MakeMesh(JsonUtility.FromJson<MeshData>(File.ReadAllText("ArtSource/HeroNinja/hero-lod2.json")),"HeroNinja_LOD2",bind);
                Shader shader=Shader.Find("Unseen/HeroNinja");if(shader==null)throw new Exception("Hero shader missing");
                var mat=new Material(shader){name="HeroNinjaMaterial",enableInstancing=true};
                mat=SaveAsset(mat,"Assets/Unseen/Resources/HeroNinjaMaterial.mat");body.sharedMaterial=mat;
                body.updateWhenOffscreen=false;body.shadowCastingMode=ShadowCastingMode.On;
                Bounds bounds=body.sharedMesh.bounds;bounds.Expand(1.0f/Mathf.Max(.001f,body.transform.lossyScale.x));body.localBounds=bounds;
                var lowHost=new GameObject("Hero body LOD1");lowHost.transform.SetParent(body.transform,false);
                var low=lowHost.AddComponent<SkinnedMeshRenderer>();low.sharedMesh=lowMesh;low.sharedMaterial=mat;low.bones=body.bones;low.rootBone=body.rootBone;
                low.localBounds=bounds;low.updateWhenOffscreen=false;low.shadowCastingMode=ShadowCastingMode.On;
                var distantHost=new GameObject("Hero body LOD2");distantHost.transform.SetParent(body.transform,false);
                var distant=distantHost.AddComponent<SkinnedMeshRenderer>();distant.sharedMesh=distantMesh;distant.sharedMaterial=mat;distant.bones=body.bones;distant.rootBone=body.rootBone;distant.localBounds=bounds;distant.shadowCastingMode=ShadowCastingMode.On;
                // Keep a low-detail silhouette visible at long range.
                var lod=subject.AddComponent<LODGroup>();lod.SetLODs(new[]{new LOD(.13f,new Renderer[]{body}),new LOD(.04f,new Renderer[]{low}),new LOD(.001f,new Renderer[]{distant})});
                lod.localReferencePoint=new Vector3(0,.9f/data.visualScale,0);lod.size=2f/data.visualScale;
                var sourceController=animator.runtimeAnimatorController;
                var overrides=new List<KeyValuePair<AnimationClip,AnimationClip>>();
                var unique=new HashSet<AnimationClip>();
                foreach(var clip in sourceController.animationClips)
                {
                    if(!unique.Add(clip))continue;
                    var copy=UnityEngine.Object.Instantiate(clip);copy.name="Hero_"+clip.name;
                    foreach(var binding in AnimationUtility.GetCurveBindings(copy))
                    {
                        if(!binding.propertyName.StartsWith("m_LocalPosition."))continue;
                        if(!originalRest.TryGetValue(binding.path,out Rest old)||!heroRest.TryGetValue(binding.path,out Rest next))continue;
                        int axis=binding.propertyName.EndsWith(".x")?0:binding.propertyName.EndsWith(".y")?1:2;
                        float ratio=old.Position.magnitude>.00001f?Mathf.Clamp(next.Position.magnitude/old.Position.magnitude,.5f,2f):1;
                        var curve=AnimationUtility.GetEditorCurve(copy,binding);var keys=curve.keys;
                        for(int i=0;i<keys.Length;i++){var k=keys[i];k.value=next.Position[axis]+(k.value-old.Position[axis])*ratio;k.inTangent*=ratio;k.outTangent*=ratio;keys[i]=k;}
                        curve.keys=keys;AnimationUtility.SetEditorCurve(copy,binding,curve);
                    }
                    copy=SaveAsset(copy,HeroDir+"/Clips/"+copy.name+".anim");overrides.Add(new KeyValuePair<AnimationClip,AnimationClip>(clip,copy));
                }
                var controller=new AnimatorOverrideController(sourceController);controller.name="HeroNinjaAnimator";controller.ApplyOverrides(overrides);
                animator.runtimeAnimatorController=SaveAsset(controller,HeroDir+"/HeroNinjaAnimator.overrideController");
                var avatar=AvatarBuilder.BuildGenericAvatar(animator.gameObject,"");avatar.name="HeroNinjaAvatar";
                animator.avatar=SaveAsset(avatar,HeroDir+"/HeroNinjaAvatar.asset");
                if(!animator.avatar.isValid)throw new Exception("Retargeted avatar is invalid");
                // Measure actual sole contact on the new proportions, not the old skeleton's drops.
                visual.CrouchBodyDrop=MeasureDrop("Hero_ninja_crouch",animator,body,heroRest);
                visual.ProneBodyDrop=MeasureDrop("Hero_ninja_prone",animator,body,heroRest);
                Restore(animator,heroRest);animator.enabled=true;
                var prefab=PrefabUtility.SaveAsPrefabAsset(subject,HeroDir+"/HeroNinja.prefab");
                var set=AssetDatabase.LoadAssetAtPath<AgentVisualSet>("Assets/Unseen/Resources/AgentVisualSet.asset");set.NinjaVisual=prefab;EditorUtility.SetDirty(set);
                AssetDatabase.SaveAssets();AssetDatabase.Refresh();
                Debug.Log($"[hero] BUILD PASS: {data.vertices.Length} vertices, {data.triangles.Length/3} triangles, {overrides.Count} retargeted clips; crouch drop {visual.CrouchBodyDrop:F3}, prone {visual.ProneBodyDrop:F3}");
            }
            finally {UnityEngine.Object.DestroyImmediate(subject);}
            CaptureAndValidate();
        }
        private static Mesh MakeMesh(MeshData data,string name,Matrix4x4[] bind)
        {
            var mesh=new Mesh{name=name,indexFormat=IndexFormat.UInt32};mesh.vertices=data.vertices;mesh.normals=data.normals;mesh.uv=data.uv;mesh.uv2=data.surface;mesh.colors=data.colors;mesh.triangles=data.triangles;
            var weights=new BoneWeight[data.weights.Length];for(int i=0;i<weights.Length;i++)
            {
                weights[i]=data.weights[i].Value;var w=weights[i];if(Mathf.Abs(w.weight0+w.weight1+w.weight2+w.weight3-1)>.001f)throw new Exception("Unnormalised skin weight");
            }
            mesh.boneWeights=weights;mesh.bindposes=bind;mesh.RecalculateBounds();mesh.RecalculateTangents();
            return SaveAsset(mesh,HeroDir+"/"+name+".asset");
        }
        private static T SaveAsset<T>(T asset,string path) where T:UnityEngine.Object
        {
            var existing=AssetDatabase.LoadAssetAtPath<T>(path);
            if(existing!=null){EditorUtility.CopySerialized(asset,existing);UnityEngine.Object.DestroyImmediate(asset);EditorUtility.SetDirty(existing);return existing;}
            AssetDatabase.CreateAsset(asset,path);return asset;
        }
        private static void Restore(Animator animator,Dictionary<string,Rest> rest)
        {
            foreach(var pair in rest)
            {
                var t=string.IsNullOrEmpty(pair.Key)?animator.transform:animator.transform.Find(pair.Key);if(t==null)continue;
                t.localPosition=pair.Value.Position;t.localRotation=pair.Value.Rotation;t.localScale=pair.Value.Scale;
            }
        }
        private static float MeasureDrop(string name,Animator animator,SkinnedMeshRenderer body,Dictionary<string,Rest> rest)
        {
            var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(HeroDir+"/Clips/"+name+".anim");if(clip==null)throw new Exception("Missing stance clip "+name);
            float sum=0;for(int i=0;i<5;i++){Restore(animator,rest);var idle=AssetDatabase.LoadAssetAtPath<AnimationClip>(HeroDir+"/Clips/Hero_idle.anim");idle.SampleAnimation(animator.gameObject,.2f);clip.SampleAnimation(animator.gameObject,clip.length*i/4);sum+=WorldBounds(body).min.y;}
            Restore(animator,rest);return Mathf.Max(0,sum/5);
        }
        private static Bounds WorldBounds(SkinnedMeshRenderer body)
        {
            var baked=new Mesh();body.BakeMesh(baked,true);var v=baked.vertices;
            var bounds=new Bounds(body.transform.TransformPoint(v[0]),Vector3.zero);foreach(var p in v)bounds.Encapsulate(body.transform.TransformPoint(p));
            UnityEngine.Object.DestroyImmediate(baked);return bounds;
        }
        public static void CaptureAndValidate()
        {
            var host=new GameObject("Hero validation");GameObject cameraHost=null;GameObject stage=null;
            try
            {
                var set=AssetDatabase.LoadAssetAtPath<AgentVisualSet>("Assets/Unseen/Resources/AgentVisualSet.asset");var visual=set.Attach(host.transform,0);var body=visual.Body;
                if(!HeroNinjaAppearance.IsHero(visual))throw new Exception("Hero prefab is not active");
                if(visual.GetComponentsInChildren<Collider>(true).Length!=0)throw new Exception("Character art contains collision");
                Bounds bound=WorldBounds(body);if(Mathf.Abs(bound.size.y-1.8f)>.035f)throw new Exception("Hero height is "+bound.size.y);
                var animator=visual.Rig;animator.enabled=false;
                var rest=new Dictionary<string,Rest>();foreach(var t in animator.GetComponentsInChildren<Transform>())rest[AnimationUtility.CalculateTransformPath(t,animator.transform)]=new Rest{Position=t.localPosition,Rotation=t.localRotation,Scale=t.localScale};
                visual.GetComponent<LODGroup>()?.ForceLOD(0);
                foreach(var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>()){skin.forceMatrixRecalculationPerRender=true;skin.updateWhenOffscreen=true;}
                stage=new GameObject("Hero studio");
                RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.30f,.34f,.42f);RenderSettings.fog=false;
                MakeLight(stage,"Studio key",new Vector3(35,-25,0),new Color(1,.88f,.75f),2.3f);
                MakeLight(stage,"Studio rim",new Vector3(25,155,0),new Color(.45f,.65f,1),1.7f);
                MakeLight(stage,"Studio fill",new Vector3(15,60,0),new Color(.70f,.80f,1),.7f);
                var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);floor.transform.SetParent(stage.transform);UnityEngine.Object.DestroyImmediate(floor.GetComponent<Collider>());
                floor.GetComponent<Renderer>().sharedMaterial=Unseen.Environment.SceneVisualUpgrade.Material("Hero studio floor",new Color(.10f,.12f,.16f),.25f);
                cameraHost=new GameObject("Hero studio camera");var camera=cameraHost.AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.065f);
                camera.nearClipPlane=.01f;camera.farClipPlane=30;camera.orthographic=true;camera.orthographicSize=1.06f;
                camera.GetUniversalAdditionalCameraData().renderShadows=true;
                camera.GetUniversalAdditionalCameraData().antialiasing=AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                Directory.CreateDirectory("ArtSource/HeroNinja/Unity");
                string[] poses={"idle","run","ninja_guard","ninja_attack_light","ninja_attack_heavy","ninja_crouch","ninja_prone","ninja_climb","ninja_hang","ninja_wallrun"};
                foreach(string pose in poses)
                {
                    var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(HeroDir+"/Clips/Hero_"+pose+".anim");if(clip==null)throw new Exception("Missing clip "+pose);
                    Restore(animator,rest);
                    var baseIdle=AssetDatabase.LoadAssetAtPath<AnimationClip>(HeroDir+"/Clips/Hero_idle.anim");
                    if(baseIdle!=null)baseIdle.SampleAnimation(animator.gameObject,.2f);
                    clip.SampleAnimation(animator.gameObject,clip.length*.35f);
                    visual.transform.localPosition=new Vector3(0,pose=="ninja_crouch"?-visual.CrouchBodyDrop:pose=="ninja_prone"?-visual.ProneBodyDrop:0,0);
                    var blade=visual.GetComponent<BladeVisual>();if(blade!=null)blade.Show(pose.Contains("attack")||pose.Contains("guard")?Unseen.Combat.BladeState.Drawn:Unseen.Combat.BladeState.Sheathed,1);
                    Bounds current=WorldBounds(body);if(current.size.magnitude>4 || float.IsNaN(current.size.x))throw new Exception("Exploded skin in "+pose);
                    camera.transform.position=new Vector3(2.4f,1.45f,4.5f);camera.transform.LookAt(new Vector3(0,.95f,0));Render(camera,"ArtSource/HeroNinja/Unity/"+pose+".png",900,1100);
                    Debug.Log($"[hero] POSE {pose}: size {current.size:F3}, floor {current.min.y:F3}");
                }
                Restore(animator,rest);visual.transform.localPosition=Vector3.zero;
                var idle=AssetDatabase.LoadAssetAtPath<AnimationClip>(HeroDir+"/Clips/Hero_idle.anim");idle.SampleAnimation(animator.gameObject,.2f);
                visual.GetComponent<BladeVisual>()?.Show(Unseen.Combat.BladeState.Sheathed,0);
                camera.orthographicSize=.33f;camera.transform.position=new Vector3(.55f,1.68f,2.0f);camera.transform.LookAt(new Vector3(0,1.57f,0));Render(camera,"ArtSource/HeroNinja/Unity/face.png",1000,1000);
                camera.orthographicSize=1.06f;camera.transform.position=new Vector3(-2,1.5f,-4);camera.transform.LookAt(new Vector3(0,.95f,0));Render(camera,"ArtSource/HeroNinja/Unity/back.png",900,1100);
                Debug.Log($"[hero] VALIDATION PASS: height {bound.size.y:F3} m, normalised weights, no colliders, ten animation poses, face and back renders");
            }
            finally {if(cameraHost!=null)UnityEngine.Object.DestroyImmediate(cameraHost);if(stage!=null)UnityEngine.Object.DestroyImmediate(stage);UnityEngine.Object.DestroyImmediate(host);}
        }
        private static void MakeLight(GameObject parent,string name,Vector3 angles,Color color,float intensity)
        {
            var go=new GameObject(name);go.transform.SetParent(parent.transform);go.transform.rotation=Quaternion.Euler(angles);var light=go.AddComponent<Light>();light.type=LightType.Directional;light.color=color;light.intensity=intensity;light.shadows=LightShadows.Soft;
        }
        private static void Render(Camera camera,string path,int width,int height)
        {
            var target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var texture=new Texture2D(width,height,TextureFormat.RGB24,false);var previous=RenderTexture.active;
            try{camera.targetTexture=target;camera.Render();camera.Render();RenderTexture.active=target;texture.ReadPixels(new Rect(0,0,width,height),0,0);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());}
            finally{camera.targetTexture=null;RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(texture);}
        }
    }
}
