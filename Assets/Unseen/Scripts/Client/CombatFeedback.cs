using System.Collections.Generic;
using UnityEngine;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Net;
using Unseen.Audio;

namespace Unseen.Client
{
    /// <summary>Short, pooled accents for directly visible combat only. Never queries hidden agents.</summary>
    public sealed class CombatFeedback : MonoBehaviour
    {
        public ClientNetworkView View;
        public ThirdPersonCameraRig CameraRig;
        private ParticleSystem _sparks;
        private Material _material;
        private AudioSource _ownSwing;
        private AudioBank _bank;
        private readonly HashSet<(int,int,int,CombatEventKind)> _seen=new HashSet<(int,int,int,CombatEventKind)>();
        private readonly Queue<(int,int,int,CombatEventKind)> _history=new Queue<(int,int,int,CombatEventKind)>();
        private sealed class Arc { public LineRenderer Line; public float Born=-10; public Vector3 Centre; public Quaternion Rotation; }
        private readonly Arc[] _arcs=new Arc[6];
        private readonly Vector3[] _points=new Vector3[14];
        private int _next;
        public int EffectsPlayed { get; private set; }
        private void Start()
        {
            Shader shader=Shader.Find("Unseen/CombatAccent");if(shader==null)return;
            _material=new Material(shader){name="Combat accents"};
            var host=new GameObject("Pooled combat sparks");host.transform.SetParent(transform,false);
            _sparks=host.AddComponent<ParticleSystem>();_sparks.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=_sparks.main;main.playOnAwake=false;main.loop=false;main.maxParticles=160;main.simulationSpace=ParticleSystemSimulationSpace.World;main.gravityModifier=.5f;
            var emission=_sparks.emission;emission.enabled=false;var shape=_sparks.shape;shape.enabled=false;
            var color=_sparks.colorOverLifetime;color.enabled=true;
            var gradient=new Gradient();gradient.SetKeys(new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},new[]{new GradientAlphaKey(1,0),new GradientAlphaKey(0,1)});color.color=gradient;
            var renderer=host.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=_material;renderer.renderMode=ParticleSystemRenderMode.Stretch;renderer.lengthScale=1.2f;renderer.velocityScale=.025f;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            for(int i=0;i<_arcs.Length;i++)
            {
                var go=new GameObject("Blade accent");go.transform.SetParent(transform,false);var line=go.AddComponent<LineRenderer>();
                line.sharedMaterial=_material;line.positionCount=_points.Length;line.useWorldSpace=true;line.startWidth=.022f;line.endWidth=.004f;line.enabled=false;
                line.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;_arcs[i]=new Arc{Line=line};
            }
            _bank=AudioBank.Load();_ownSwing=gameObject.AddComponent<AudioSource>();_ownSwing.playOnAwake=false;_ownSwing.spatialBlend=0;_ownSwing.volume=.25f;
            if(View!=null)View.SnapshotApplied+=OnSnapshot;
        }
        public static bool Direct(SnapshotData snapshot,AgentId id)
        {
            if(!id.IsValid)return false;if(id==snapshot.SelfId)return true;
            for(int i=0;i<snapshot.Entities.Count;i++)if(snapshot.Entities[i].Id==id)return (snapshot.Entities[i].Kind&VisibilityKind.Direct)!=0;
            return false;
        }
        private void OnSnapshot(SnapshotData snapshot)
        {
            Camera camera=CameraRig!=null?CameraRig.GetComponent<Camera>():Camera.main;if(camera==null)return;
            foreach(CombatEvent e in snapshot.Combat)
            {
                bool swing=e.Kind==CombatEventKind.Swing;
                if(!swing&&e.Kind!=CombatEventKind.Hit&&e.Kind!=CombatEventKind.Blocked&&e.Kind!=CombatEventKind.Parried&&e.Kind!=CombatEventKind.GuardBroken)continue;
                var key=(e.Tick,e.Attacker.Value,e.Victim.Value,e.Kind);if(!_seen.Add(key))continue;
                _history.Enqueue(key);if(_history.Count>256)_seen.Remove(_history.Dequeue());
                if(!Direct(snapshot,swing?e.Attacker:e.Victim))continue;
                Vector3 point=(Vector3)e.Position+(swing?Vector3.up*1.1f:Vector3.zero);
                if((camera.transform.position-point).sqrMagnitude>45*45)continue;
                if(Physics.Linecast(camera.transform.position,point,UnseenLayers.WorldGeometry,QueryTriggerInteraction.Ignore))continue;
                if(swing)
                {
                    float yaw=snapshot.SelfYaw;
                    foreach(var entity in snapshot.Entities)if(entity.Id==e.Attacker){yaw=entity.Yaw;break;}
                    Arc arc=_arcs[_next++%_arcs.Length];arc.Centre=point;arc.Rotation=Quaternion.Euler(e.Zone==GuardZone.High?-35:e.Zone==GuardZone.Low?30:0,yaw,0);arc.Born=Time.time;
                    if(e.Attacker==snapshot.SelfId && _bank!=null)
                    {
                        var entry=_bank.For(SoundKind.WeaponSwing);var clip=entry!=null?AudioBank.Pick(entry.Clips):null;
                        if(clip!=null)_ownSwing.PlayOneShot(clip,.6f);
                    }
                }
                else
                {
                    bool metal=e.Kind!=CombatEventKind.Hit;
                    int count=e.Kind==CombatEventKind.Parried?16:metal?9:5;
                    for(int i=0;i<count;i++)
                    {
                        Vector3 direction=new Vector3(Mathf.Sin(i*2.4f),.3f+(i%3)*.2f,Mathf.Cos(i*2.4f));
                        var emit=new ParticleSystem.EmitParams{position=point,velocity=direction*(metal?2.1f:.6f),startLifetime=metal ? .24f:.18f,startSize=metal ? .025f:.035f,startColor=metal?new Color(1,.58f,.19f,1):new Color(.42f,.40f,.36f,.5f)};
                        _sparks.Emit(emit,1);
                    }
                    if(e.Victim==snapshot.SelfId&&CameraRig!=null)CameraRig.AddImpact(e.Kind==CombatEventKind.Parried ? .3f:.6f);
                }
                EffectsPlayed++;
            }
        }
        private void Update()
        {
            for(int i=0;i<_arcs.Length;i++)
            {
                Arc arc=_arcs[i];if(arc==null)continue;float t=(Time.time-arc.Born)/.14f;
                arc.Line.enabled=t>=0&&t<1;if(!arc.Line.enabled)continue;
                for(int j=0;j<_points.Length;j++)
                {
                    float angle=Mathf.Lerp(-1.1f,1.1f,j/(float)(_points.Length-1))+t*.5f;
                    _points[j]=arc.Centre+arc.Rotation*new Vector3(Mathf.Sin(angle)*.85f,.12f*Mathf.Sin(angle),Mathf.Cos(angle)*.85f);
                }
                arc.Line.SetPositions(_points);arc.Line.startColor=new Color(.55f,.7f,.8f,(1-t)*.4f);arc.Line.endColor=new Color(.7f,.82f,1,0);
            }
        }
        private void OnDestroy(){if(View!=null)View.SnapshotApplied-=OnSnapshot;if(_material!=null)Destroy(_material);}
    }
}
