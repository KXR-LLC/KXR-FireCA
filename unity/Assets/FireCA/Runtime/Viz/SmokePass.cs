using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FireCA
{
    /// <summary>
    /// The Smoke Pass: the HDRP Custom Pass that draws the smoke volume (docs/04-visualization.md).
    /// The pass draws a full-screen ray march through the Smoke texture of the Wind Grid, over the camera color.
    /// </summary>
    [Serializable]
    public sealed class SmokePass : CustomPass
    {
        static readonly int SmokeParamsId = Shader.PropertyToID("_FireCA_SmokeParams");
        static readonly int SunDirectionId = Shader.PropertyToID("_FireCA_SunDirection");
        static readonly int SunColorId = Shader.PropertyToID("_FireCA_SunColor");
        static readonly int SkyColorId = Shader.PropertyToID("_FireCA_SkyColor");

        /// <summary>The material of FireCA/SmokePass.</summary>
        public Material material;

        protected override void Execute(CustomPassContext ctx)
        {
            FireCAApp app = FireCAApp.Instance;
            if (material == null || app == null || app.Scenario == null || app.Wind == null || !app.Wind.Enabled) return;
            if (!app.SmokeVisible || app.SmokeDensity <= 0f) return;

            WeatherModel weather = app.Weather;
            Vector3 sun = weather.SunDirection;
            Light light = app.SunLight;
            Color sunColor = light != null ? light.color.linear * light.intensity : Color.black;
            // Ambient light on the smoke: a share of the sky by day, and a small value at night.
            float day = Mathf.Clamp01(sun.y * 4f);
            Color sky = Color.Lerp(new Color(2f, 2.5f, 4f), new Color(3500f, 4200f, 5200f), day);

            ctx.propertyBlock.SetVector(SmokeParamsId, new Vector4(app.SmokeDensity, 0f, 0f, 0f));
            ctx.propertyBlock.SetVector(SunDirectionId, new Vector4(sun.x, Mathf.Max(sun.y, 0.02f), sun.z, 0f));
            ctx.propertyBlock.SetVector(SunColorId, sunColor);
            ctx.propertyBlock.SetVector(SkyColorId, sky);
            CoreUtils.DrawFullScreen(ctx.cmd, material, ctx.propertyBlock, shaderPassId: 0);
        }
    }
}
