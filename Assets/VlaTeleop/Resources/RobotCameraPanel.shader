// Robot camera panel — unlit video quad that ignores depth.
//
// The point cloud (RobotPointCloudOverlay) is a VOLUME of geometry sitting a
// metre or two ahead of the user, so any panel placed inside that range gets
// partly swallowed by it no matter where you put it. Raising the panel helps;
// drawing it with ZTest Always makes it impossible to lose.
//
// Built-in RP (QuestTeleop is not URP). In Resources/ so a built APK finds it.
Shader "VlaTeleop/RobotCameraPanel"
{
    Properties
    {
        _MainTex ("Video", 2D) = "black" {}
    }
    SubShader
    {
        // Overlay queue + no depth test/write: always on top of the cloud, and
        // never occludes anything itself.
        Tags { "Queue" = "Overlay" "RenderType" = "Overlay" "IgnoreProjector" = "True" }
        ZTest Always
        ZWrite Off
        Cull Off
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // Quest renders stereo SINGLE-PASS INSTANCED: both eyes come from
            // one draw call, distinguished by the instance id. A shader without
            // these macros writes only eye 0 — the panel appears in the LEFT eye
            // and is simply absent from the right. (Unlit/Texture, which this
            // replaced, carries them via UnityCG; a hand-written shader must
            // opt in. Both RobotPointCloud shaders do the same.)
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
    Fallback "Unlit/Texture"
}
