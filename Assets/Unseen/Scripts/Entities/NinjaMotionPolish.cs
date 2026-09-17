using System;
using System.Collections.Generic;
using UnityEngine;
using Unseen.Core;
using Unseen.Combat;

namespace Unseen.Entities
{
    /// <summary>Additive presentation only: Blender-authored envelopes, weight shifts and a creeping gait.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class NinjaMotionPolish : MonoBehaviour
    {
        [Serializable] private sealed class Envelopes { public float[] landing, breathing, attack; }
        private static Envelopes _curves;
        private AgentEntity _agent;
        private AgentVisual _visual;
        private Quaternion _restRotation;
        private Vector3 _offset, _lastVelocity;
        private float _lean, _bank, _landTime=10, _landingStrength, _gait, _attackTime=10;
        private bool _wasGrounded=true;
        private AttackPhase _phase;
        private struct BoneOffset { public Transform Bone; public Quaternion Rotation; public Vector3 Axis; public bool Left; public bool Knee; }
        private readonly List<BoneOffset> _bones=new List<BoneOffset>();
        private bool _posed;
        private void Awake()
        {
            _visual=GetComponent<AgentVisual>();_agent=GetComponentInParent<AgentEntity>();_restRotation=transform.localRotation;
            if(_curves==null)
            {
                var data=Resources.Load<TextAsset>("BlenderArt/UpgradeMotion");
                if(data!=null){_curves=JsonUtility.FromJson<Envelopes>(data.text);Resources.UnloadAsset(data);}
            }
            foreach(Transform bone in GetComponentsInChildren<Transform>())
                if(bone.name=="LeftUpLeg"||bone.name=="RightUpLeg"||bone.name=="LeftLeg"||bone.name=="RightLeg")
                    _bones.Add(new BoneOffset{Bone=bone,Axis=bone.InverseTransformDirection(transform.right),Left=bone.name.StartsWith("Left"),Knee=!bone.name.Contains("Up")});
        }
        private void Update(){Restore();}
        private void Restore()
        {
            if(!_posed)return;
            transform.localPosition-=_offset;transform.localRotation=_restRotation;_offset=Vector3.zero;
            for(int i=0;i<_bones.Count;i++){var b=_bones[i];if(b.Bone!=null)b.Bone.localRotation=b.Rotation;}
            _posed=false;
        }
        private void LateUpdate()
        {
            if(_agent==null)_agent=GetComponentInParent<AgentEntity>();
            if(_agent==null||!_agent.IsAlive||_agent.Motor==null||_visual==null||_visual.Body==null)return;
            float dt=Time.deltaTime;if(dt<=0)return;
            Vector3 velocity=(Vector3)_agent.Motor.Velocity;
            bool ground=_agent.Motor.IsGrounded;
            if(ground&&!_wasGrounded){_landTime=0;_landingStrength=Mathf.Clamp01(-_lastVelocity.y/9f);}
            _wasGrounded=ground;_landTime+=dt;
            Vector3 local=transform.parent.InverseTransformDirection(velocity);
            float acceleration=Mathf.Clamp((local.z-transform.parent.InverseTransformDirection(_lastVelocity).z)/Mathf.Max(.01f,dt),-8,8);
            _lastVelocity=velocity;
            bool free=_agent.Locomotion==LocomotionState.Grounded && _agent.Stance!=Stance.Prone;
            float blend=1-Mathf.Exp(-10*dt);
            _lean=Mathf.Lerp(_lean,free?Mathf.Clamp(local.z*.5f+acceleration*.25f,-4,5):0,blend);
            _bank=Mathf.Lerp(_bank,free?Mathf.Clamp(-local.x*.7f,-3,3):0,blend);
            AttackPhase phase=_agent.Melee!=null?_agent.Melee.Phase:AttackPhase.Idle;
            if(phase==AttackPhase.Windup&&_phase!=phase)_attackTime=0;
            _phase=phase;_attackTime+=dt;
            float landing=Sample(_curves?.landing,_landTime/.58f)*_landingStrength;
            float attack=Sample(_curves?.attack,_attackTime/.65f);
            float breath=Sample(_curves?.breathing,Mathf.Repeat(Time.time+_agent.Id.Value*.37f,3.2f)/3.2f);
            float dip=free?-.028f*landing:0;
            // The body stays at its authored stance height; subtle shifts are restored before the next animation update.
            _offset=transform.parent.InverseTransformVector(Vector3.up*(dip+(free ? .0025f*breath:0)));
            transform.localPosition+=_offset;
            transform.localRotation=_restRotation*Quaternion.Euler(_lean+landing*2.5f+attack*1.5f,0,_bank);
            float speed=new Vector2(velocity.x,velocity.z).magnitude;
            _gait+=speed*dt*5.5f;
            bool creeping=free&&ground&&_agent.Stance==Stance.Crouch&&phase==AttackPhase.Idle;
            for(int i=0;i<_bones.Count;i++)
            {
                var b=_bones[i];if(b.Bone==null)continue;b.Rotation=b.Bone.localRotation;
                if(creeping)
                {
                    float stride=Mathf.Sin(_gait+(b.Left?0:Mathf.PI))*Mathf.Clamp01(speed/.9f);
                    float angle=b.Knee?Mathf.Max(0,-stride)*13:stride*9;
                    b.Bone.localRotation*=Quaternion.AngleAxis(angle,b.Axis);
                }
                _bones[i]=b;
            }
            _posed=true;
        }
        private static float Sample(float[] values,float t)
        {
            if(values==null||values.Length<2||t<0||t>=1)return 0;
            float frame=t*(values.Length-1);int i=(int)frame;return Mathf.Lerp(values[i],values[i+1],frame-i);
        }
        private void OnDisable(){Restore();}
    }
}
