// VLA XR teleop — robot POV point cloud, DISCRETE POINTS variant (built-in RP).
//
// Same unprojection as RobotPointCloud.shader, but renders each depth pixel as
// a small camera-facing billboard QUAD instead of connecting neighbours into a
// surface — i.e. a literal point cloud floating in the VR user's space. Fat
// billboards (not GL points) because Quest's Vulkan backend clamps gl_PointSize
// to 1 px, which is invisible in a headset.
//
// Mesh contract (built by RobotPointCloudOverlay in Points mode): 4 verts per
// depth pixel, vertex.xy = the quad corner in {-0.5,+0.5}, uv = the pixel's
// normalized image coord (y down from top, shared by all 4 corners). The
// vertex shader unprojects the pixel to camera-local space, then offsets the
// corner by _PointSize metres in VIEW space so every point faces the eye.
//
// Lives in Resources/ so Resources.Load ships it in the APK.
Shader "VlaTeleop/RobotPointCloudPoints"
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
        _PointSize ("point size (m)", Float) = 0.012
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
            float _HasColor, _PointSize, _ColorRange;

            #define DEPTH_METERS(uv) (tex2Dlod(_DepthTex, float4(uv, 0, 0)).r * 65.535)

            struct appdata
            {
                float4 vertex : POSITION;   // xy = quad corner in {-0.5,+0.5}
                float2 uv : TEXCOORD0;      // pixel normalized coord, y down
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 cuv : TEXCOORD0;
                float valid : TEXCOORD1;
                float z : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float2 duv = v.uv;
                float z = DEPTH_METERS(duv);
                if (z <= 0.001)
                {
                    o.pos = float4(0, 0, -2, 1);   // cull invalid pixel
                    o.cuv = 0; o.valid = 0; o.z = 0;
                    return o;
                }

                float u_pix = duv.x * _TexW;
                float v_pix = duv.y * _TexH;
                float xo = (u_pix - _Cx) / _Fx * z;
                float yo = (v_pix - _Cy) / _Fy * z;
                float3 localOptical = float3(xo, -yo, z);   // Unity camera axes

                float4 viewPos = mul(UNITY_MATRIX_MV, float4(localOptical, 1));
                viewPos.xy += v.vertex.xy * _PointSize;      // camera-facing billboard
                o.pos = mul(UNITY_MATRIX_P, viewPos);
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
                float t = saturate(i.z / _ColorRange);
                fixed3 c = lerp(fixed3(0.25, 0.85, 1.0), fixed3(1.0, 0.45, 0.15), t);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
