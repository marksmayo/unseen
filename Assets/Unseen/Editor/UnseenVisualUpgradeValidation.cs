using System;
using UnityEditor;
using UnityEngine;
using Unseen.Client;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Net;

namespace Unseen.EditorTools
{
    public static class UnseenVisualUpgradeValidation
    {
        public static void Run()
        {
            ValidateMeshes();
            ValidateVisibility();
            UnseenNinjaArtExport.ValidateAndRender();
            UnseenCameraProbe.Run();
            Debug.Log("[visual-upgrade] PASS: mesh payloads, visibility gate, skinning, garments, poses and camera collision");
        }
        private static void ValidateMeshes()
        {
            foreach(string name in new[]{"UpgradeStorageJar","UpgradeBasket","UpgradeTimberStack","UpgradePouch","UpgradeBracer","UpgradeTabi","UpgradeCorbel","UpgradeTileEdge","UpgradeShojiFrame","UpgradeBanner","UpgradeLeafLitter"})
            {
                Mesh mesh=BlenderArt.Get(name);
                if(mesh==null||mesh.vertexCount==0||mesh.vertexCount>10000)throw new Exception("Invalid Blender mesh: "+name);
                if(mesh.bounds.size.magnitude>6)throw new Exception("Unexpected Blender scale: "+name);
                foreach(Vector3 v in mesh.vertices)if(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z))throw new Exception("Nonfinite mesh: "+name);
            }
            var host=new GameObject("Visual collision check");
            try
            {
                var mat=SceneVisualUpgrade.Material("Validation",Color.gray);
                SceneVisualUpgrade.Detail(host.transform,"UpgradeStorageJar",Vector3.zero,Vector3.one,Quaternion.identity,mat);
                if(host.GetComponentsInChildren<Collider>().Length!=0)throw new Exception("Visual detail added collision");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);}
            Debug.Log("[visual-upgrade] PASS: eleven Blender meshes, metre scale, finite geometry, no detail colliders");
        }
        private static void ValidateVisibility()
        {
            var snapshot=new SnapshotData{SelfId=new AgentId(1)};
            var hidden=new AgentId(2);
            if(CombatFeedback.Direct(snapshot,hidden))throw new Exception("Hidden combat leaked");
            snapshot.Entities.Add(new VisibleEntity{Id=hidden,Kind=VisibilityKind.Silhouette});
            if(CombatFeedback.Direct(snapshot,hidden))throw new Exception("Silhouette combat leaked");
            snapshot.Entities[0]=new VisibleEntity{Id=hidden,Kind=VisibilityKind.Proximate};
            if(CombatFeedback.Direct(snapshot,hidden))throw new Exception("Proximate combat leaked");
            snapshot.Entities[0]=new VisibleEntity{Id=hidden,Kind=VisibilityKind.Direct};
            if(!CombatFeedback.Direct(snapshot,hidden)||!CombatFeedback.Direct(snapshot,snapshot.SelfId))throw new Exception("Visible combat suppressed");
            Debug.Log("[visual-upgrade] PASS: effects accept direct/self contacts, reject hidden/proximate/silhouette contacts");
        }
    }
}
