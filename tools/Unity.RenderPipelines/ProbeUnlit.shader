Shader "SocketJack/CompatibilityProbeUnlit" {
    Properties { _Color ("Color", Color) = (1,1,1,1) }
    SubShader { Tags { "RenderType"="Opaque" } Pass {
        Tags { "LightMode"="SRPDefaultUnlit" }
        HLSLPROGRAM
        #pragma vertex Vert
        #pragma fragment Frag
        #include "UnityCG.cginc"
        float4 _Color;
        struct VertexOutput { float4 position : SV_POSITION; };
        VertexOutput Vert(float4 vertex : POSITION) { VertexOutput output; output.position = UnityObjectToClipPos(vertex); return output; }
        float4 Frag(VertexOutput input) : SV_Target { return _Color; }
        ENDHLSL
    } }
}
