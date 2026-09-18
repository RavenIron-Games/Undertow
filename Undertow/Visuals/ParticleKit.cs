using System;
using UnityEngine;
using RavenIron.Undertow.Core;

namespace RavenIron.Undertow.Visuals
{
    /// <summary>
    /// Shader resolution and material generation for Undertow's client-side visuals.
    ///
    /// The candidate-shader chain is ported VERBATIM from Ragnarok's Wrath's ParticleKit and
    /// must never fork: it encodes a lesson that cost RW a release (its 0.7.0) — Valheim strips
    /// Unity's standard particle shaders from its build, so a mod that asks for
    /// "Particles/Standard Unlit" and stops there draws nothing, anywhere, silently. Every
    /// candidate is tried before giving up, the chosen one is logged under the owner's name
    /// (two clients disagreeing about a visual is otherwise undiagnosable), and a genuine
    /// absence throws so the caller's build path can latch.
    ///
    /// NO ASSETS. The texture is rasterised from <see cref="DriftLineMath.StreakAlphaAt"/> and
    /// the material is constructed here; nothing is shipped, bundled or registered with
    /// ZNetScene. The material is OURS — house rule 4 forbids touching vanilla's water material,
    /// shader or mesh, and nothing in this file reads them.
    /// </summary>
    internal static class ParticleKit
    {
        /// <summary>
        /// Shaders Valheim's build might actually contain, in soft-haze-first order.
        /// "Particles/Standard Unlit" is CONFIRMED stripped from Valheim's build, kept first
        /// only for future Unity versions; "Sprites/Default" is the first that ships.
        /// </summary>
        private static readonly string[] CandidateShaders =
        {
            "Particles/Standard Unlit",
            "Legacy Shaders/Particles/Alpha Blended",
            "Sprites/Default",
            "UI/Default",
            "Particles/Standard Surface",
            "Legacy Shaders/Particles/Additive",
        };

        public const int StreakTextureWidth  = 128;
        public const int StreakTextureHeight = 32;
        public const int StreakTextureSeed   = 7;

        /// <summary>
        /// After vanilla's transparent water (Unity's Transparent queue is 3000) so a streak is
        /// never tinted or hidden by the water's own blend. Set on OUR material only.
        /// </summary>
        public const int RenderQueue = 3100;

        /// <summary>The one place the candidate chain lives — see the class comment.</summary>
        public static Shader FindShader(string owner, out string chosenName)
        {
            foreach (string name in CandidateShaders)
            {
                Shader shader = Shader.Find(name);
                if (shader != null)
                {
                    chosenName = name;
                    Undertow.Log.LogInfo($"{owner}: using shader '{name}'.");
                    return shader;
                }
            }
            throw new InvalidOperationException("no particle shader available");
        }

        /// <summary>
        /// Whether a resolved shader applies Unity's fog itself. The legacy particle shaders
        /// compile multi_compile_fog; Sprites/Default and UI/Default do not, and a streak on
        /// those would stand out of the mist unless the caller fogs it by hand.
        /// </summary>
        public static bool ShaderHasFog(string shaderName)
            => shaderName != null
               && (shaderName.StartsWith("Legacy Shaders/Particles/", StringComparison.Ordinal)
                   || shaderName.StartsWith("Particles/", StringComparison.Ordinal));

        /// <summary>
        /// The streak material: a 128x32 feathered, mottled, end-to-end symmetric alpha mask,
        /// white so the emitter tints entirely through vertex colour. Throws when no shader
        /// resolves — the caller owns the latch.
        /// </summary>
        public static Material BuildStreakMaterial(string owner, out string shaderName, out bool shaderHasFog)
        {
            Shader shader = FindShader(owner, out shaderName);
            shaderHasFog = ShaderHasFog(shaderName);

            const int w = StreakTextureWidth;
            const int h = StreakTextureHeight;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "undertow_drift_line",
            };

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float a = DriftLineMath.StreakAlphaAt(x, y, w, h, StreakTextureSeed);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();

            var material = new Material(shader)
            {
                mainTexture = tex,
                name = "undertow_drift_line",
                renderQueue = RenderQueue,
            };
            return material;
        }
    }
}
