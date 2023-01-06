/// BRDF Lit
#ifndef CUSTOM_LIT_PASS_INCLUDED
#define CUSTOM_LIT_PASS_INCLUDED

// #include "../ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "../ShaderLibrary/Surface.hlsl"
#include "../ShaderLibrary/Shadows.hlsl"
#include "../ShaderLibrary/Light.hlsl"
#include "../ShaderLibrary/BRDF.hlsl"
#include "../ShaderLibrary/GlobalIllumination.hlsl"
#include "../ShaderLibrary/Lighting.hlsl"

// We have reorganized the property below in "LitInput.hlsl", where is before this shader.
/*
TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
UNITY_INSTANCING_BUFFER_START(UnityPerMaterial)
	UNITY_DEFINE_INSTANCED_PROP(float4, _BaseMap_ST)		
    UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
	UNITY_DEFINE_INSTANCED_PROP(float, _Cutoff)			
	UNITY_DEFINE_INSTANCED_PROP(float, _Metallic)
	UNITY_DEFINE_INSTANCED_PROP(float, _Smoothness)	
UNITY_INSTANCING_BUFFER_END(UnityPerMaterial)
*/

/// We need to know the object index when we enable GPU Instancing with "UnityInstancing.hlsl",
/// in where it also assumes that our vertex function has a struct parameter.
/// So we define a struct here - just like a cbuffer,
/// and put in the object index attribute "UNITY_VERTEX_INPUT_INSTANCE_ID"
struct Attributes {
	float3 positionOS : POSITION;
	float3 normalOS : NORMAL;
	float2 baseUV : TEXCOORD0;
	UNITY_VERTEX_INPUT_INSTANCE_ID
	GI_ATTRIBUTE_DATA
};

/// Varyings contains the data can vary between fragments of the same triangle.
struct Varyings  {
	float4 positionCS : SV_POSITION;
	float3 positionWS : VAR_POSITION;
	float3 normalWS : VAR_NORMAL;
	float2 baseUV : VAR_BASE_UV;
    UNITY_VERTEX_INPUT_INSTANCE_ID      
	GI_VARYINGS_DATA
};

Varyings LitPassVertex (Attributes input)
{
    Varyings output;
	// Extracts the index from the input and stores it in a global static variable that the other instancing macros rely on.
    UNITY_SETUP_INSTANCE_ID(input);                                     
	// Copy the index when it exists.          
	UNITY_TRANSFER_INSTANCE_ID(input, output);   
	TRANSFER_GI_DATA(input, output);                     
	output.positionWS = TransformObjectToWorld(input.positionOS);  
	output.positionCS = TransformWorldToHClip(output.positionWS);
	output.baseUV = TransformBaseUV(input.baseUV);		// (LitInput.hlsl)
	output.normalWS = TransformObjectToWorldNormal(input.normalOS);

    return output;
}

float4 LitPassFragment (Varyings input) : SV_TARGET 
{
	UNITY_SETUP_INSTANCE_ID(input);
	float4 base = GetBase(input.baseUV);					// Get Blend result (LitInput.hlsl)
	#if defined(_CLIPPING)									// Alpha clipping
		clip(base.a - GetCutoff(input.baseUV));				// (LitInput.hlsl)
	#endif

	// Visualize normal length error caused by linear interpolation distortion
	// base.rgb = abs(length(input.normalWS) - 1.0) * 10.0;			

	// Smooth out the interpolation distortion 				
	// base.rgb = normalize(input.normalWS);		
	
	Surface surface;
	surface.position = input.positionWS;
	surface.normal = normalize(input.normalWS);		
	surface.viewDirection = normalize(_WorldSpaceCameraPos - input.positionWS);		
	surface.depth = -TransformWorldToView(input.positionWS).z;						
	surface.color = base.rgb;
	surface.alpha = base.a;
	surface.metallic = GetMetallic(input.baseUV);			// (LitInput.hlsl)
	surface.smoothness = GetSmoothness(input.baseUV);		// (LitInput.hlsl)
	surface.dither = InterleavedGradientNoise(input.positionCS.xy, 0);

	#if defined(_PREMULTIPLY_ALPHA)
		BRDF brdf = GetBRDF(surface, true);
	#else
		BRDF brdf = GetBRDF(surface);
	#endif

	GI gi = GetGI(GI_FRAGMENT_DATA(input), surface);
	float3 color = GetLighting(surface, brdf, gi);
	color += GetEmission(input.baseUV);

	return float4(color, surface.alpha);
}

#endif