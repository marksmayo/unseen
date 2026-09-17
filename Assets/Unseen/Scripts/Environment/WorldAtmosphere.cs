using System.Collections.Generic;
using UnityEngine;
using Unseen.Perception;

namespace Unseen.Environment
{
    /// <summary>One wind clock and a capped set of lantern shell animations; gameplay anchors never move.</summary>
    public sealed class WorldAtmosphere : MonoBehaviour
    {
        private struct Lamp { public Transform Shell; public bool Lit; public Light Light; public StealthLightSource Source; public float Intensity; public Renderer Renderer; }
        private readonly List<Lamp> _lamps=new List<Lamp>();
        private MaterialPropertyBlock _properties;
        private Camera _camera;
        private static readonly int Wind=Shader.PropertyToID("_UnseenWind");
        public static Vector3 Breeze => new Vector3(.8f,0,.35f);
        public void Collect(Transform root)
        {
            _properties=new MaterialPropertyBlock();
            foreach(var lantern in root.GetComponentsInChildren<Lantern>())
            {
                Transform shell=lantern.transform.Find("Shell");
                var light=lantern.GetComponent<Light>();
                if(shell!=null && light!=null) _lamps.Add(new Lamp{Shell=shell,Lit=false,Light=light,Source=lantern.Source,Intensity=light.intensity,Renderer=shell.GetComponent<Renderer>()});
            }
            Shader.SetGlobalVector(Wind,new Vector4(.8f,.35f,.25f,0));
        }
        private void LateUpdate()
        {
            float t=Time.time;
            Shader.SetGlobalVector(Wind,new Vector4(.8f,.35f,.23f+.08f*Mathf.Sin(t*.31f),t));
            if(_camera==null)_camera=Camera.main;
            if(_camera==null)return;
            int animated=0;
            for(int i=0;i<_lamps.Count;i++)
            {
                Lamp lamp=_lamps[i];if(lamp.Shell==null||lamp.Light==null)continue;
                bool lit=lamp.Source!=null&&!lamp.Source.Extinguished;
                // Doused shells lose their emissive glow as well as their point light.
                if(lamp.Renderer!=null && lamp.Lit!=lit)
                {
                    lamp.Renderer.GetPropertyBlock(_properties);
                    _properties.SetColor("_EmissionColor",lit?new Color(1f,.46f,.13f)*1.1f:Color.black);
                    lamp.Renderer.SetPropertyBlock(_properties);
                    lamp.Lit=lit; _lamps[i]=lamp;
                }
                if(!lit || !lamp.Light.enabled || animated>=32 || (lamp.Shell.position-_camera.transform.position).sqrMagnitude>1600)continue;
                float phase=i*2.399f;
                lamp.Light.intensity=lamp.Intensity*(.97f+.025f*Mathf.Sin(t*4.1f+phase)+.015f*Mathf.Sin(t*9.7f+phase));
                // Tiny light variation shares the wind clock without moving gameplay anchors.
                animated++;
            }
        }
        private void OnDisable(){Shader.SetGlobalVector(Wind,Vector4.zero);}
    }
}
