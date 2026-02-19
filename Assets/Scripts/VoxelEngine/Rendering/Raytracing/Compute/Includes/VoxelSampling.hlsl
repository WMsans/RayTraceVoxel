float SampleBrick(uint brickOffset, uint brickPtr, float3 localPos) 
{ 
    float3 p = localPos + (float)BRICK_PADDING;
    int3 i = clamp((int3)floor(p), 0, BRICK_STORAGE_SIZE - 2); 
    float3 f = p - (float3)i;
    uint baseIdx = brickOffset + brickPtr + (i.z * BRICK_STORAGE_SIZE * BRICK_STORAGE_SIZE) + (i.y * BRICK_STORAGE_SIZE) + i.x;
    uint sliceStride = BRICK_STORAGE_SIZE * BRICK_STORAGE_SIZE; 
    float s000, s001, s010, s011, s100, s101, s110, s111; 
    float3 n; 
    uint m;
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx], s000, n, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + 1], s001, n, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + BRICK_STORAGE_SIZE], s010, n, m);
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + BRICK_STORAGE_SIZE + 1], s011, n, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride], s100, n, m);
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride + 1], s101, n, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride + BRICK_STORAGE_SIZE], s110, n, m);
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride + BRICK_STORAGE_SIZE + 1], s111, n, m);
    return lerp(lerp(lerp(s000, s001, f.x), lerp(s010, s011, f.x), f.y), lerp(lerp(s100, s101, f.x), lerp(s110, s111, f.x), f.y), f.z);
}

float3 GetVoxelNormal(uint brickOffset, uint brickPtr, float3 localPos) 
{ 
    float3 p = localPos + (float)BRICK_PADDING;
    int3 i = clamp((int3)floor(p), 0, BRICK_STORAGE_SIZE - 2); 
    float3 f = p - (float3)i;
    uint baseIdx = brickOffset + brickPtr + (i.z * BRICK_STORAGE_SIZE * BRICK_STORAGE_SIZE) + (i.y * BRICK_STORAGE_SIZE) + i.x;
    uint sliceStride = BRICK_STORAGE_SIZE * BRICK_STORAGE_SIZE; 
    float s; 
    float3 v000, v001, v010, v011, v100, v101, v110, v111; 
    uint m;
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx], s, v000, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + 1], s, v001, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + BRICK_STORAGE_SIZE], s, v010, m);
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + BRICK_STORAGE_SIZE + 1], s, v011, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride], s, v100, m);
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride + 1], s, v101, m); 
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride + BRICK_STORAGE_SIZE], s, v110, m);
    UnpackVoxelData(_GlobalBrickDataBuffer[baseIdx + sliceStride + BRICK_STORAGE_SIZE + 1], s, v111, m); 
    
    float3 mixX00 = lerp(v000, v001, f.x);
    float3 mixX01 = lerp(v010, v011, f.x); 
    float3 mixX10 = lerp(v100, v101, f.x); 
    float3 mixX11 = lerp(v110, v111, f.x);
    float3 mixY0 = lerp(mixX00, mixX01, f.y); 
    float3 mixY1 = lerp(mixX10, mixX11, f.y); 
    
    return normalize(lerp(mixY0, mixY1, f.z));
}

uint GetVoxelMaterial(uint brickOffset, uint brickPtr, float3 localPos) 
{ 
    float3 p = localPos + (float)BRICK_PADDING;
    uint3 i = clamp((uint3)floor(p + 0.5), 0, BRICK_STORAGE_SIZE - 1);
    uint idx = brickOffset + brickPtr + i.z * BRICK_STORAGE_SIZE * BRICK_STORAGE_SIZE + i.y * BRICK_STORAGE_SIZE + i.x; 
    
    float s;
    float3 n; 
    uint mat; 
    UnpackVoxelData(_GlobalBrickDataBuffer[idx], s, n, mat); 
    return mat;
}
