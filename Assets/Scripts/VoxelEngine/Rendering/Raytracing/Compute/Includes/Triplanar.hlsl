void TriplanarSampling(float3 worldPos, float3 normal, uint matId, out float3 outAlbedo, out float3 outNormal, out float outRoughness, out float outMetallic, out float outAO, float lod) 
{ 
    if (matId == 0) 
    { 
        outAlbedo = 1;
        outNormal = normal; 
        outRoughness = 0.5; 
        outMetallic = 0; 
        outAO = 1; 
        return;
    } 
    
    float texScale = _RaytraceParams.w;

    VoxelTypeGPU mat = _VoxelMaterialBuffer[matId];
    float3 weights = abs(normal);
    weights = pow(weights, 8.0); 
    float weightSum = weights.x + weights.y + weights.z;
    weights /= (weightSum + 1e-5);
    float3 accumAlbedo = 0; 
    float accumAO = 0; 
    float accumRoughness = 0;
    float accumMetallic = 0;
    if (weights.x > 0.01) 
    { 
        float2 uv = worldPos.zy * texScale;
        float3 col = _AlbedoTextureArray.SampleLevel(sampler_LinearRepeat, float3(uv, mat.sideAlbedoIndex), lod).rgb;
        float4 mask = _MaskTextureArray.SampleLevel(sampler_LinearRepeat, float3(uv, mat.sideMaskIndex), lod); 
        
        accumAlbedo += col * weights.x;
        accumAO += mask.g * weights.x;
        accumRoughness += mask.b * weights.x; 
        accumMetallic += mat.sideMetallic * weights.x;
    } 
    
    if (weights.y > 0.01) 
    { 
        float2 uv = worldPos.xz * texScale;
        uint albedoID = (normal.y > 0) ? mat.topAlbedoIndex : mat.sideAlbedoIndex;
        uint maskID = (normal.y > 0) ? mat.topMaskIndex : mat.sideMaskIndex;
        float met = (normal.y > 0) ? mat.topMetallic : mat.sideMetallic;
        float3 col = _AlbedoTextureArray.SampleLevel(sampler_LinearRepeat, float3(uv, albedoID), lod).rgb;
        float4 mask = _MaskTextureArray.SampleLevel(sampler_LinearRepeat, float3(uv, maskID), lod); 
        
        accumAlbedo += col * weights.y;
        accumAO += mask.g * weights.y;
        accumRoughness += mask.b * weights.y; 
        accumMetallic += met * weights.y;
    } 
    
    if (weights.z > 0.01) 
    { 
        float2 uv = worldPos.xy * texScale;
        float3 col = _AlbedoTextureArray.SampleLevel(sampler_LinearRepeat, float3(uv, mat.sideAlbedoIndex), lod).rgb;
        float4 mask = _MaskTextureArray.SampleLevel(sampler_LinearRepeat, float3(uv, mat.sideMaskIndex), lod); 
        
        accumAlbedo += col * weights.z;
        accumAO += mask.g * weights.z;
        accumRoughness += mask.b * weights.z; 
        accumMetallic += mat.sideMetallic * weights.z;
    } 
    
    outAlbedo = accumAlbedo;
    outAO = accumAO; 
    outRoughness = accumRoughness;
    outMetallic = accumMetallic; 
    outNormal = normal;
}
