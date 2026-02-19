// -----------------------------------------------------------------------------
// Lighting Functions
// -----------------------------------------------------------------------------

// [SHADOWS] New function to sample Unity's Global Shadow Map
float GetUnityShadow(float3 positionWS)
{
    float3 fromCam = positionWS - _CameraToWorld._m03_m13_m23;
    float distSq = dot(fromCam, fromCam);
    
    int cascadeIndex = 0;
    // Check splits (w is radius squared)
    if(distSq > _CascadeShadowSplitSpheres0.w) cascadeIndex = 1;
    if(distSq > _CascadeShadowSplitSpheres1.w) cascadeIndex = 2;
    if(distSq > _CascadeShadowSplitSpheres2.w) cascadeIndex = 3;
    
    // Project World Position to Shadow Map Texture Space
    float4 shadowCoord = mul(_MainLightWorldToShadow[cascadeIndex], float4(positionWS, 1.0));
    // Perspective division (usually 1.0 for Directional, but important for correctness)
    shadowCoord.xyz /= shadowCoord.w;
    
    // Check bounds
    if (shadowCoord.x < 0 || shadowCoord.x > 1 || shadowCoord.y < 0 || shadowCoord.y > 1) 
        return 1.0;
        
    // Sample Shadow Map (using Point Sampler)
    float shadowMapDepth = _MainLightShadowmapTexture.SampleLevel(sampler_CameraDepthTexture, shadowCoord.xy, 0).r;
    
    // Bias to prevent self-shadowing artifacts
    float bias = 0.005;

    // Compare Depth
    // UNITY_REVERSED_Z is standard in URP for modern platforms (1 = Near, 0 = Far)
#if UNITY_REVERSED_Z
    if(shadowCoord.z < shadowMapDepth - bias) return 0.0; // Occluded
#else
    if(shadowCoord.z > shadowMapDepth + bias) return 0.0;
#endif

    return 1.0;
}

float DistributionGGX(float3 N, float3 H, float roughness) 
{ 
    float a = roughness * roughness;
    float a2 = a * a; 
    float NdotH = max(dot(N, H), 0.0); 
    float NdotH2 = NdotH * NdotH;
    float nom = a2; 
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom; 
    return nom / max(denom, 0.0000001);
}

float GeometrySchlickGGX(float NdotV, float roughness) 
{ 
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    float nom = NdotV;
    float denom = NdotV * (1.0 - k) + k; 
    return nom / max(denom, 0.0000001);
}

float GeometrySmith(float3 N, float3 V, float3 L, float roughness) 
{ 
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0); 
    float ggx2 = GeometrySchlickGGX(NdotV, roughness); 
    float ggx1 = GeometrySchlickGGX(NdotL, roughness); 
    return ggx1 * ggx2;
}

float3 FresnelSchlick(float cosTheta, float3 F0) 
{ 
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

float3 GetSkyColor(float3 rayDir) 
{ 
    float3 skyUp = float3(0.1, 0.3, 0.6); 
    float3 skyHorizon = float3(0.6, 0.7, 0.8);
    float3 ground = float3(0.05, 0.04, 0.04); 
    
    float y = rayDir.y; 
    float3 sky = lerp(skyHorizon, skyUp, pow(max(y, 0.0), 0.5));
    float3 finalColor = lerp(ground, sky, smoothstep(-0.1, 0.1, y)); 
    return finalColor;
}

float3 LightingPBR(float3 albedo, float3 N, float3 V, float3 L, float3 lightColor, float roughness, float metallic, float ao) 
{ 
    roughness = max(roughness, 0.05);
    float3 H = normalize(V + L); 
    float NdotL = max(dot(N, L), 0.0); 
    float3 F0 = float3(0.04, 0.04, 0.04);
    F0 = lerp(F0, albedo, metallic); 
    
    float NDF = DistributionGGX(N, H, roughness); 
    float G = GeometrySmith(N, V, L, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0); 
    
    float3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * NdotL + 0.0001; 
    float3 specular = numerator / denominator;
    float3 kS = F; 
    float3 kD = float3(1.0, 1.0, 1.0) - kS; 
    kD *= 1.0 - metallic;
    float3 direct = (kD * albedo / PI + specular) * lightColor * NdotL * 3;
    float3 envColor = GetSkyColor(reflect(-V, N)); 
    float3 ambient = envColor * albedo * ao * 0.5;
    float rimVal = 1.0 - max(dot(V, N), 0.0); 
    rimVal = smoothstep(0.5, 1.0, rimVal);
    float3 rim = rimVal * float3(0.5, 0.6, 0.8) * ao * 0.2; 
    
    return ambient + direct + rim;
}

float3 LightingDirect(float3 albedo, float3 N, float3 V, float3 L, float3 lightColor, float roughness, float metallic, float attenuation)
{
    roughness = max(roughness, 0.05);
    float3 H = normalize(V + L);

    // [CEL SHADING]
    float NdotL_Raw = max(dot(N, L), 0.0) * attenuation;
    float steps = max(1.0, _CelShadeParams.x);
    float minBrightness = _CelShadeParams.y;
    float t = NdotL_Raw * steps;
    float stepIndex = floor(t);
    float fraction = t - stepIndex;
    float smoothFraction = smoothstep(0.0, 0.05 * steps, fraction);
    float rawLevel = (stepIndex + smoothFraction) / steps;
    float steppedNdotL = lerp(minBrightness, 1.0, saturate(rawLevel));
    float3 F0 = float3(0.04, 0.04, 0.04);
    F0 = lerp(F0, albedo, metallic); 

    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
    float3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * NdotL_Raw + 0.0001;
    float3 specular = numerator / denominator;
    float specLum = dot(specular, float3(0.2126, 0.7152, 0.0722));
    float specThreshold = 0.5 - (roughness * 0.3);
    float steppedSpec = smoothstep(specThreshold, specThreshold + 0.05, specLum);

    float3 finalSpecular = steppedSpec * lightColor * 5.0 * attenuation;
    float3 kS = F; 
    float3 kD = float3(1.0, 1.0, 1.0) - kS; 
    kD *= 1.0 - metallic;
    return (kD * albedo / PI * steppedNdotL) * lightColor * 3.0 + finalSpecular;
}
