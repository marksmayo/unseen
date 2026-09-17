using UnityEngine;
using Unseen.Environment;

namespace Unseen.Entities
{
    /// <summary>Small Blender-authored garments, fitted in metres to the original skeleton.</summary>
    public static class NinjaEquipmentArt
    {
        public static void Fit(AgentVisual visual)
        {
            Transform root=visual.transform;
            Material cloth=SceneVisualUpgrade.Material("Upgrade Equipment Cloth",new Color(.09f,.11f,.14f),.12f);
            Material armour=SceneVisualUpgrade.Material("Upgrade Lacquer Armour",new Color(.07f,.085f,.11f),.32f,.25f);
            Material leather=SceneVisualUpgrade.Material("Upgrade Oiled Leather",new Color(.14f,.085f,.055f),.24f);
            foreach(Transform bone in root.GetComponentsInChildren<Transform>())
            {
                if(bone.name=="Hips") Attach(root,bone,"UpgradePouch",bone.position-root.right*.20f-root.forward*.075f,Quaternion.LookRotation(root.forward,root.up),Vector3.one,leather);
                if(bone.name=="LeftForeArm"||bone.name=="RightForeArm")
                {
                    string handName=bone.name.StartsWith("Left")?"LeftHand":"RightHand";
                    Transform hand=null;foreach(Transform child in bone.GetComponentsInChildren<Transform>())if(child.name==handName){hand=child;break;}
                    if(hand==null)continue;
                    Vector3 axis=(hand.position-bone.position).normalized;
                    Vector3 forward=Vector3.ProjectOnPlane(root.forward,axis).normalized;
                    if(forward.sqrMagnitude<.1f)forward=Vector3.ProjectOnPlane(root.right,axis).normalized;
                    Attach(root,bone,"UpgradeBracer",Vector3.Lerp(bone.position,hand.position,.22f)+forward*.055f,Quaternion.LookRotation(forward,axis),Vector3.one,armour);
                }
                if(bone.name=="LeftFoot"||bone.name=="RightFoot")
                {
                    // Blender toe direction is -Z after export. Align it with character forward.
                    Attach(root,bone,"UpgradeTabi",bone.position-root.up*.035f,Quaternion.LookRotation(-root.forward,root.up),Vector3.one,cloth);
                }
            }
        }
        private static void Attach(Transform root,Transform bone,string mesh,Vector3 pos,Quaternion rot,Vector3 metres,Material mat)
        {
            Transform piece=SceneVisualUpgrade.Detail(bone,mesh,Vector3.zero,Vector3.one,Quaternion.identity,mat,true);
            if(piece==null)return;
            piece.position=pos;piece.rotation=rot;
            Vector3 scale=bone.lossyScale;
            piece.localScale=new Vector3(metres.x/Mathf.Max(.0001f,Mathf.Abs(scale.x)),metres.y/Mathf.Max(.0001f,Mathf.Abs(scale.y)),metres.z/Mathf.Max(.0001f,Mathf.Abs(scale.z)));
            piece.gameObject.layer=root.gameObject.layer;
        }
    }
}
