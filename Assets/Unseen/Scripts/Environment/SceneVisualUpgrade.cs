using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unseen.Core;

namespace Unseen.Environment
{
    /// <summary>Bounded, renderer-only dressing on the generated town. Never changes collision or stealth.</summary>
    public static class SceneVisualUpgrade
    {
        public static int LastDetails { get; private set; }
        public static int LastPropGroups { get; private set; }
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

        public static Material Material(string name, Color color, float smoothness = .2f, float metal = 0f)
        {
            if (Materials.TryGetValue(name, out Material existing) && existing != null) return existing;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            var m = new Material(shader) { name = name, enableInstancing = true };
            m.SetColor("_BaseColor", color); m.SetFloat("_Smoothness", smoothness); m.SetFloat("_Metallic", metal);
            Materials[name] = m;
            return m;
        }

        public static Transform Detail(Transform parent, string meshName, Vector3 position, Vector3 scale,
            Quaternion rotation, Material material, bool moving = false)
        {
            Mesh mesh = BlenderArt.Get(meshName);
            if (mesh == null || material == null) return null;
            var host = new GameObject(meshName);
            host.layer = UnseenLayers.Decoration;
            host.transform.SetParent(parent, false);
            host.transform.localPosition = position; host.transform.localRotation = rotation; host.transform.localScale = scale;
            host.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = host.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            host.isStatic = !moving;
            LastDetails++;
            return host.transform;
        }

        public static void Apply(Transform town, int seed)
        {
            if (town.Find("VisualUpgrade") != null) return;
            LastDetails = LastPropGroups = 0;
            var host = new GameObject("VisualUpgrade").transform; host.SetParent(town, false);
            Material ceramic = Material("Upgrade Ceramic", new Color(.095f,.13f,.18f), .36f);
            Material timber = Material("Upgrade Aged Timber", new Color(.16f,.105f,.065f));
            Material clay = Material("Upgrade Clay", new Color(.25f,.115f,.065f), .22f);
            Material straw = Material("Upgrade Straw", new Color(.27f,.22f,.12f), .12f);
            Material leaves = Material("Upgrade Fallen Leaves", new Color(.17f,.105f,.045f));
            Material fabric = WeatheredMaterials.WindCloth(Material("Upgrade Indigo Banner", new Color(.075f,.105f,.16f)));
            var random = new System.Random(seed ^ 0x51A7);
            Physics.SyncTransforms();
            BoxCollider[] solids = town.GetComponentsInChildren<BoxCollider>();
            int roofRuns = 0, brackets = 0;
            foreach (BoxCollider solid in solids)
            {
                string n = solid.name;
                // Existing roof tops and their collision remain intact. Ceramic edges fit inside the eave.
                if (n.StartsWith("Roof_") && n.EndsWith("_0") && roofRuns < 300)
                {
                    Vector3 size = solid.size;
                    if (size.x < 2 || size.z < 2) continue;
                    for (int side=0; side<4 && roofRuns<300; side++)
                    {
                        float span = side%2==0 ? size.x : size.z;
                        int sections = Mathf.Clamp(Mathf.FloorToInt(span/4f), 1, 8);
                        float length = Mathf.Min(4f, span/sections);
                        for (int j=0;j<sections && roofRuns<300;j++)
                        {
                            float along = (j-(sections-1)*.5f)*length;
                            Quaternion rot = Quaternion.Euler(0,side*90,0);
                            float half = (side%2==0 ? size.z : size.x)*.5f;
                            Vector3 pos = rot * new Vector3(along,0,-half+.03f) + solid.center + Vector3.up*(size.y*.5f+.015f);
                            Detail(solid.transform,"UpgradeTileEdge",pos,new Vector3(length/4,1,1),rot,ceramic);
                            roofRuns++;
                        }
                    }
                    if (brackets < 100)
                    {
                        for (int side=0;side<4;side++)
                        {
                            Quaternion rot=Quaternion.Euler(0,90*side,0);
                            float half=(side%2==0?size.z:size.x)*.5f;
                            Detail(solid.transform,"UpgradeCorbel",solid.center+rot*new Vector3(0,-.65f,-half+.3f),new Vector3(.65f,.7f,.8f),rot,timber);
                            brackets++;
                        }
                    }
                }
                Bounds bounds=solid.bounds;
                if (LastPropGroups>=100 || !n.Contains("Wall") || solid.isTrigger || bounds.min.y>.7f || bounds.size.y<2.2f) continue;
                bool alongX=bounds.size.x>bounds.size.z;
                if (Mathf.Max(bounds.size.x,bounds.size.z)<5) continue;
                Vector3 normal=alongX?Vector3.forward:Vector3.right;
                Vector3 tangent=alongX?Vector3.right:Vector3.forward;
                float halfDepth=(alongX?bounds.extents.z:bounds.extents.x);
                float halfLength=alongX?bounds.extents.x:bounds.extents.z;
                Vector3 candidate=bounds.center+normal*(halfDepth+.56f)+tangent*(halfLength*.58f*(random.Next(2)==0?-1:1));
                candidate.y=.7f;
                if (!Physics.Raycast(candidate+Vector3.up*2,Vector3.down,out RaycastHit floor,4,UnseenLayers.WorldGeometry,QueryTriggerInteraction.Ignore)) continue;
                if (floor.normal.y<.9f || floor.point.y<-.15f || floor.point.y>.6f) continue;
                candidate.y=floor.point.y;
                if (Physics.CheckBox(candidate+Vector3.up*.3f,new Vector3(.32f,.26f,.32f),Quaternion.identity,UnseenLayers.WorldGeometry,QueryTriggerInteraction.Ignore)) continue;
                var cluster = new GameObject("Courtyard prop group").transform;cluster.SetParent(host,false);cluster.position=candidate;
                cluster.rotation=Quaternion.LookRotation(normal);
                string prop=LastPropGroups%3==0?"UpgradeStorageJar":LastPropGroups%3==1?"UpgradeBasket":"UpgradeTimberStack";
                Detail(cluster,prop,Vector3.zero,Vector3.one,Quaternion.Euler(0,random.Next(-15,16),0),prop.Contains("Jar")?clay:prop.Contains("Basket")?straw:timber);
                Detail(cluster,"UpgradeLeafLitter",new Vector3(0,.007f,.26f),Vector3.one,Quaternion.Euler(0,random.Next(360),0),leaves);
                // Banners sit on the wall above these edge-of-street groups, clear of doors and routes.
                if (LastPropGroups%4==0)
                    Detail(cluster,"UpgradeBanner",new Vector3(0,1.8f,-.47f),new Vector3(.55f,.85f,1),Quaternion.identity,fabric,true);
                LastPropGroups++;
            }
            // Give the flat emissive keep windows tangible frames. Window collisions are unchanged.
            var windows = town.GetComponentsInChildren<MeshRenderer>();
            int framed=0;
            foreach (MeshRenderer window in windows)
            {
                if (!window.name.StartsWith("KeepWindow_") || framed>=80) continue;
                Bounds b=window.localBounds;
                bool xFacing=b.size.x<b.size.z;
                Quaternion rot=Quaternion.Euler(0,xFacing?90:0,0);
                // Both faces are dressed; one lies just inside the wall and cannot protrude into a route.
                for(int face=-1;face<=1;face+=2)
                {
                    Vector3 local= xFacing?new Vector3(face*(b.extents.x+.015f),-b.extents.y,0):new Vector3(0,-b.extents.y,face*(b.extents.z+.015f));
                    Detail(window.transform,"UpgradeShojiFrame",local,new Vector3(xFacing?b.size.z:b.size.x,b.size.y,1),rot,timber);
                }
                framed++;
            }
            if (town.GetComponent<WorldAtmosphere>() == null) town.gameObject.AddComponent<WorldAtmosphere>().Collect(town);
            Debug.Log($"[visual-upgrade] {LastDetails} detail meshes; {LastPropGroups} supported prop groups; {roofRuns} ceramic runs; {framed} framed windows");
        }
    }
}
