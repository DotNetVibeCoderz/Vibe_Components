namespace OpenSceneGraph.Net.Rendering;

/// <summary>
/// GLSL sources shared by the OpenGL (desktop / ANGLE) and WebGL2 renderers. Bodies are GLSL ES 3.00 compatible;
/// renderers prepend <see cref="HeaderEs"/> or <see cref="HeaderCore"/>.
///
/// Custom <see cref="State.ShaderProgram"/>s use the same interface:
/// attributes <c>osg_Vertex</c> (location 0), <c>osg_Normal</c> (1), <c>osg_Color</c> (2), <c>osg_MultiTexCoord0</c> (3);
/// uniforms <c>osg_ModelMatrix</c>, <c>osg_ViewMatrix</c>, <c>osg_ProjectionMatrix</c>, <c>osg_NormalMatrix</c> (mat3),
/// <c>osg_SimulationTime</c>, <c>osg_ViewportSize</c>, <c>osg_EyePosition</c>, plus any values from the state set's uniforms.
/// </summary>
public static class ShaderLibrary
{
    public const string HeaderEs = "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp sampler2D;\n";
    public const string HeaderCore = "#version 330 core\n";

    public const int MaxLights = 8;

    public const string MainVertex = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aColor;
        layout(location = 3) in vec2 aUv;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform mat3 uNormalMat;
        uniform mat4 uLightSpace;
        uniform float uPointSize;
        uniform int uPointMode;      // 0 pixel size, 1 world size from aUv.x (particles)
        uniform float uViewportH;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec4 vColor;
        out vec2 vUv;
        out vec4 vLightSpace;
        out float vViewDepth;
        void main() {
            vec4 w = uModel * vec4(aPos, 1.0);
            vec4 v = uView * w;
            vWorld = w.xyz;
            vNormal = uNormalMat * aNormal;
            vColor = aColor;
            vUv = aUv;
            vLightSpace = uLightSpace * w;
            vViewDepth = -v.z;
            gl_Position = uProj * v;
            if (uPointMode == 1) {
                gl_PointSize = clamp(aUv.x * uProj[1][1] * uViewportH * 0.5 / max(-v.z, 0.001), 1.0, 256.0);
            } else {
                gl_PointSize = uPointSize;
            }
        }
        """;

    public const string MainFragment = """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec4 vColor;
        in vec2 vUv;
        in vec4 vLightSpace;
        in float vViewDepth;
        uniform vec4 uDiffuse;
        uniform vec3 uMatAmbient;
        uniform vec3 uSpecular;
        uniform vec3 uEmission;
        uniform float uShininess;
        uniform float uReflectivity;
        uniform int uLighting;
        uniform int uHasVertexColor;
        uniform int uHasTexture;
        uniform sampler2D uTex;
        uniform int uLightCount;
        uniform vec4 uLightPos[8];    // xyz position or direction, w: 0 directional, 1 point, 2 spot
        uniform vec4 uLightColor[8];  // rgb * intensity, a = range
        uniform vec4 uLightDir[8];    // xyz spot direction, w = cos(cutoff)
        uniform vec3 uAmbient;
        uniform vec3 uEye;
        uniform vec3 uEnvColor;
        uniform int uFog;
        uniform vec3 uFogColor;
        uniform vec2 uFogRange;
        uniform int uShadow;
        uniform sampler2D uShadowMap;
        uniform float uShadowBias;
        uniform float uShadowTexel;
        uniform int uPointMode;
        uniform int uIsPoints;
        out vec4 fragColor;

        float shadowFactor(vec3 n, vec3 l) {
            vec3 p = vLightSpace.xyz / vLightSpace.w;
            p = p * 0.5 + 0.5;
            if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0 || p.z > 1.0) return 1.0;
            float bias = max(uShadowBias * 4.0 * (1.0 - dot(n, l)), uShadowBias);
            float lit = 0.0;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++) {
                    float d = texture(uShadowMap, p.xy + vec2(float(x), float(y)) * uShadowTexel).r;
                    lit += (p.z - bias) > d ? 0.0 : 1.0;
                }
            return mix(0.35, 1.0, lit / 9.0);
        }

        void main() {
            vec4 base = uDiffuse;
            if (uHasVertexColor == 1) base *= vColor;
            if (uHasTexture == 1) base *= texture(uTex, vUv);
            if (uIsPoints == 1 && uPointMode == 1) {
                vec2 pc = gl_PointCoord * 2.0 - 1.0;
                float r = dot(pc, pc);
                if (r > 1.0) discard;
                base.a *= 1.0 - smoothstep(0.35, 1.0, r);
            }
            if (base.a < 0.004) discard;

            vec3 color;
            if (uLighting == 1) {
                vec3 n = normalize(vNormal);
                if (!gl_FrontFacing) n = -n;
                vec3 v = normalize(uEye - vWorld);
                vec3 diffuse = vec3(0.0);
                vec3 spec = vec3(0.0);
                for (int i = 0; i < 8; i++) {
                    if (i >= uLightCount) break;
                    vec3 l;
                    float att = 1.0;
                    if (uLightPos[i].w < 0.5) {
                        l = -normalize(uLightPos[i].xyz);
                    } else {
                        vec3 d = uLightPos[i].xyz - vWorld;
                        float dist = length(d);
                        l = d / max(dist, 1e-4);
                        float range = max(uLightColor[i].a, 1e-3);
                        att = clamp(1.0 - (dist * dist) / (range * range), 0.0, 1.0);
                        att *= att;
                        if (uLightPos[i].w > 1.5) {
                            float c = dot(-l, normalize(uLightDir[i].xyz));
                            att *= smoothstep(uLightDir[i].w, mix(uLightDir[i].w, 1.0, 0.25), c);
                        }
                    }
                    float ndl = max(dot(n, l), 0.0);
                    float sh = (i == 0 && uShadow == 1) ? shadowFactor(n, l) : 1.0;
                    diffuse += uLightColor[i].rgb * ndl * att * sh;
                    vec3 h = normalize(l + v);
                    spec += uLightColor[i].rgb * pow(max(dot(n, h), 0.0), max(uShininess, 1.0)) * att * sh * step(0.0, ndl);
                }
                color = base.rgb * (uAmbient + uMatAmbient * 0.5 + diffuse) + uSpecular * spec + uEmission;
                if (uReflectivity > 0.0) {
                    float fres = pow(1.0 - max(dot(n, v), 0.0), 3.0);
                    vec3 r = reflect(-v, n);
                    vec3 env = mix(uEnvColor * 0.35, uEnvColor, clamp(r.z * 0.5 + 0.5, 0.0, 1.0));
                    color = mix(color, env, clamp(uReflectivity * (0.25 + 0.75 * fres), 0.0, 1.0));
                }
            } else {
                color = base.rgb + uEmission;
            }
            if (uFog == 1) {
                float f = clamp((vViewDepth - uFogRange.x) / max(uFogRange.y - uFogRange.x, 1e-3), 0.0, 1.0);
                color = mix(color, uFogColor, f);
            }
            fragColor = vec4(color, base.a);
        }
        """;

    public const string DepthVertex = """
        layout(location = 0) in vec3 aPos;
        uniform mat4 uLightMvp;
        void main() { gl_Position = uLightMvp * vec4(aPos, 1.0); }
        """;

    public const string DepthFragment = """
        out vec4 fragColor;
        void main() { fragColor = vec4(1.0); }
        """;

    /// <summary>Full-screen triangle generated from gl_VertexID (no vertex buffer needed).</summary>
    public const string FullscreenVertex = """
        out vec2 vUv;
        void main() {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    public const string BackgroundFragment = """
        in vec2 vUv;
        uniform vec4 uTop;
        uniform vec4 uBottom;
        out vec4 fragColor;
        void main() { fragColor = mix(uBottom, uTop, smoothstep(0.0, 1.0, vUv.y)); }
        """;

    public const string BrightFragment = """
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform float uThreshold;
        out vec4 fragColor;
        void main() {
            vec3 c = texture(uScene, vUv).rgb;
            float l = max(max(c.r, c.g), c.b);
            fragColor = vec4(c * smoothstep(uThreshold, uThreshold + 0.25, l), 1.0);
        }
        """;

    public const string BlurFragment = """
        in vec2 vUv;
        uniform sampler2D uSrc;
        uniform vec2 uDir;
        out vec4 fragColor;
        void main() {
            vec3 c = texture(uSrc, vUv).rgb * 0.227027;
            c += texture(uSrc, vUv + uDir * 1.3846).rgb * 0.3162162;
            c += texture(uSrc, vUv - uDir * 1.3846).rgb * 0.3162162;
            c += texture(uSrc, vUv + uDir * 3.2308).rgb * 0.0702703;
            c += texture(uSrc, vUv - uDir * 3.2308).rgb * 0.0702703;
            fragColor = vec4(c, 1.0);
        }
        """;

    public const string CompositeFragment = """
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uBloom;
        uniform int uUseBloom;
        uniform float uBloomIntensity;
        uniform int uHdr;
        uniform float uExposure;
        uniform int uVignette;
        out vec4 fragColor;
        vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }
        void main() {
            vec3 c = texture(uScene, vUv).rgb;
            if (uUseBloom == 1) c += texture(uBloom, vUv).rgb * uBloomIntensity;
            if (uHdr == 1) c = aces(c * uExposure);
            if (uVignette == 1) {
                vec2 d = vUv - 0.5;
                c *= 1.0 - smoothstep(0.35, 0.85, length(d)) * 0.55;
            }
            fragColor = vec4(c, 1.0);
        }
        """;
}
