// VLA XR teleop — robot POV point cloud (built-in RP, Quest-friendly).
//
// Renders a static W x H grid mesh whose vertices are placed by THIS shader:
// each vertex carries its depth-image pixel coordinate in uv0, samples the
// uploaded VLAD depth texture (R16, millimeters/65535) in the vertex stage,
// and unprojects through the streamed pinhole intrinsics into camera-local
// space (Unity axes: X right, Y up, Z forward = optical axis). The owning
// RobotPointCloudOverlay places the mesh's transform at the robot camera's
// pose, so triangles land where the robot actually saw geometry.
//
// Invalid pixels (depth 0) and depth discontinuities (silhouette edges) are
// culled by collapsing the vertex outside clip space + fragment discard —
// this "tears" the rubber-sheet at object boundaries instead of bridging
// foreground to background.
//
// Lives in Resources/ so Resources.Load ships it in the APK (Shader.Find on
// an unreferenced shader would come back null in a device build).
Shader "VlaTeleop/RobotPointCloud"
{
    Properties
    {
        _DepthTex ("Depth (R16 mm)", 2D) = "black" {}
        _ColorTex ("Color", 2D) = "gray" {}
        _Fx ("fx px", Float) = 100
        _Fy ("fy px", Float) = 100
        _Cx ("cx px", Float) = 96
        _Cy ("cy px", Float) = 54
        _TexW ("depth width", Float) = 192
        _TexH ("depth height", Float) = 108
        _HasColor ("use color tex", Float) = 0
        _TearFrac ("tear threshold (fraction of z)", Float) = 0.06
        _ColorRange ("colormap far (m)", Float) = 6
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Single Pass Instanced stereo (OpenXR renderMode 1 = what this
            // project ships). Without these the eye matrices never resolve and
            // the cloud is invisible in the Game view / headset while still
            // drawing fine in the mono Scene view.
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _DepthTex;
            sampler2D _ColorTex;
            float _Fx, _Fy, _Cx, _Cy, _TexW, _TexH;
            float _HasColor, _TearFrac, _ColorRange;

            // R16 unorm sample -> meters (payload is uint16 millimeters).
            #define DEPTH_METERS(uv) (tex2Dlod(_DepthTex, float4(uv, 0, 0)).r * 65.535)

            struct appdata
            {
                float4 vertex : POSITION;      // unused (grid placeholder)
                float2 uv : TEXCOORD0;         // normalized image coords, y DOWN from top
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 cuv : TEXCOORD0;        // color-texture uv
                float valid : TEXCOORD1;
                float z : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // Depth texture rows are uploaded in wire order (row 0 = image
                // top) so texture v == v.uv.y directly; the JPEG color texture
                // decodes bottom-up, so its v is flipped.
                float2 duv = v.uv;
                float z = DEPTH_METERS(duv);

                float2 px = float2(1.0 / _TexW, 1.0 / _TexH);
                float zR = DEPTH_METERS(duv + float2(px.x, 0));
                float zD = DEPTH_METERS(duv + float2(0, px.y));
                float tear = max(0.02, _TearFrac * z);
                bool ok = z > 0.001 &&
                          abs(zR - z) < tear &&
                          abs(zD - z) < tear;

                if (!ok)
                {
                    o.pos = float4(0, 0, -2, 1);   // outside clip on GL and D3D-style
                    o.cuv = float2(0, 0);
                    o.valid = 0;
                    o.z = 0;
                    return o;
                }

                float u_pix = duv.x * _TexW;
                float v_pix = duv.y * _TexH;       // from image TOP
                float xo = (u_pix - _Cx) / _Fx * z;
                float yo = (v_pix - _Cy) / _Fy * z;  // optical Y is DOWN
                float3 local = float3(xo, -yo, z);   // Unity camera axes

                o.pos = UnityObjectToClipPos(float4(local, 1));
                o.cuv = float2(duv.x, 1.0 - duv.y);
                o.valid = 1;
                o.z = z;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                if (i.valid < 0.999) discard;
                if (_HasColor > 0.5)
                    return fixed4(tex2D(_ColorTex, i.cuv).rgb, 1);
                // No RGB overlay running: cheap near-cyan -> far-orange ramp.
                float t = saturate(i.z / _ColorRange);
                fixed3 c = lerp(fixed3(0.25, 0.85, 1.0), fixed3(1.0, 0.45, 0.15), t);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
