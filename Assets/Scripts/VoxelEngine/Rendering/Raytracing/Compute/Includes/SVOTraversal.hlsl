// -----------------------------------------------------------------------------
// Ray Traversal
// -----------------------------------------------------------------------------

struct HitInfo 
{
    bool hit; 
    float3 pos;
    float3 normal; 
    uint matId; 
    bool isLOD; 
    float3 lodColor;
    uint brickId; 
    int chunkId;
};
HitInfo TraceSVO(ChunkDef chunk, float3 rayOriginNode, float3 rayDir, float maxDist, float tStart)
{
    float tCurrent = tStart;
    float tEnd = maxDist;
    int iter = 0;
    float gridSz = 64.0;

    HitInfo result;
    result.hit = false; 
    result.pos = 0;
    result.normal = 0;
    result.matId = 0;
    result.isLOD = false;
    result.lodColor = 0;
    result.brickId = 0;
    result.chunkId = -1;
    while (tCurrent < tEnd && iter < _MaxIterations)
    {
        iter++;
        float3 pos = rayOriginNode + rayDir * (tCurrent + EPSILON);
        uint nodeIndex = 0;
        float nodeSize = gridSz;
        float3 nodePos = float3(0,0,0);
        bool hitLeaf = false;

        for (int depth = 0; depth < 5; depth++)
        {
            float halfSize = nodeSize * 0.5;
            float3 center = nodePos + halfSize;
            int octant = 0;
            
            if (pos.x >= center.x) octant |= 1;
            if (pos.y >= center.y) octant |= 2;
            if (pos.z >= center.z) octant |= 4;

            SVONode node = _GlobalNodeBuffer[GetPhysicalIndex(nodeIndex, chunk.pageTableOffset, _PageTableBuffer)];
            uint childMask = (node.topology >> 24) & 0xFF;

            if ((childMask & (1 << octant)) != 0)
            {
                uint childBase = node.topology & 0xFFFFFF;
                uint maskBefore = childMask & ((1 << octant) - 1);
                uint childNodeIndex = childBase + countbits(maskBefore);
                float3 childPos = nodePos + float3((octant & 1) ? halfSize : 0, (octant & 2) ? halfSize : 0, (octant & 4) ? halfSize : 0);
                nodeIndex = childNodeIndex;
                nodeSize = halfSize;
                nodePos = childPos;
                if (nodeSize <= BRICK_SIZE + EPSILON) 
                { 
                    hitLeaf = true;
                    break;
                }
            }
            else
            {
                float3 octantMin = nodePos + float3((octant & 1) ? halfSize : 0, (octant & 2) ? halfSize : 0, (octant & 4) ? halfSize : 0);
                float2 tOctant = IntersectBox(rayOriginNode, rayDir, octantMin, octantMin + halfSize);
                tCurrent = tOctant.y; 
                break;
            }
        }

        if (hitLeaf)
        {
            SVONode leafNode = _GlobalNodeBuffer[GetPhysicalIndex(nodeIndex, chunk.pageTableOffset, _PageTableBuffer)];
            float2 tLeaf = IntersectBox(rayOriginNode, rayDir, nodePos, nodePos + nodeSize);

            uint payloadIndex, matID;
            UnpackNode(leafNode, payloadIndex, matID);
            if (payloadIndex != 0)
            {
                VoxelPayload payload = _GlobalPayloadBuffer[GetPhysicalIndex(payloadIndex, chunk.payloadPageTableOffset, _PageTableBuffer)];
                uint brickPtr = payload.brickDataIndex;

                float tMarch = max(tCurrent, tLeaf.x);
                float marchEnd = min(tEnd, tLeaf.y);
                for (int m = 0; m < _MaxMarchSteps; m++)
                {
                    if (tMarch >= marchEnd) break;
                    float3 p = rayOriginNode + rayDir * tMarch;
                    float3 localPos = p - nodePos;
                    float dist = SampleBrick(chunk.brickOffset, brickPtr, localPos);
                    if (dist < EPSILON)
                    {
                        float tMin = tMarch - 0.05;
                        float tMax = tMarch;
                        for(int r = 0; r < 4; r++) 
                        {
                            float tMid = (tMin + tMax) * 0.5;
                            float dMid = SampleBrick(chunk.brickOffset, brickPtr, (rayOriginNode + rayDir * tMid) - nodePos);
                            if (dMid < EPSILON) tMax = tMid;
                            else tMin = tMid;
                        }
                        
                        tMarch = tMax;
                        p = rayOriginNode + rayDir * tMarch;
                        localPos = p - nodePos;

                        result.hit = true;
                        result.pos = p;
                        result.normal = GetVoxelNormal(chunk.brickOffset, brickPtr, localPos);
                        result.matId = GetVoxelMaterial(chunk.brickOffset, brickPtr, localPos);
                        result.brickId = brickPtr;
                        return result;
                    }
                    tMarch += max(dist, 0.001);
                }
            }
            tCurrent = tLeaf.y + EPSILON;
        }
    }
    return result;
}

HitInfo TraceScene(float3 rayOrigin, float3 rayDir, float maxDist)
{
    float closestT = maxDist;
    HitInfo bestHit = (HitInfo)0;
    bestHit.hit = false;
    bestHit.chunkId = -1;

    float2 tScene = IntersectBox(rayOrigin, rayDir, _TLASBoundsMin, _TLASBoundsMax);
    if (tScene.x < tScene.y && tScene.y > 0)
    {
        if (_ChunkCount < 64)
        {
            float tCurrent = max(0.0, tScene.x);
            float tEnd = min(closestT, tScene.y);

            for (int k = 0; k < _ChunkCount; k++)
            {
                ChunkDef chunk = _ChunkBuffer[k];
                float2 tBox = IntersectBox(rayOrigin, rayDir, chunk.boundsMin, chunk.boundsMax);
                
                if (tBox.x < tBox.y && tBox.x < closestT && tBox.y > tCurrent)
                {
                    float3 localOrigin = mul(chunk.worldToLocal, float4(rayOrigin, 1.0)).xyz;
                    float3 localDir = normalize(mul((float3x3)chunk.worldToLocal, rayDir));
                    float boundsSz = 64.0;
                    float2 tLocalBox = IntersectBox(localOrigin, localDir, float3(0,0,0), float3(boundsSz, boundsSz, boundsSz));
                    if (tLocalBox.x < tLocalBox.y && tLocalBox.y > 0)
                    {
                        float tStartL = max(0.0, tLocalBox.x);
                        float tEndL = tLocalBox.y;
                        HitInfo info = TraceSVO(chunk, localOrigin, localDir, tEndL, tStartL);
                        if (info.hit)
                        {
                            float3 hitWorld = mul(chunk.localToWorld, float4(info.pos, 1.0)).xyz;
                            float dist = length(hitWorld - rayOrigin);
                            
                            if (dist < closestT)
                            {
                                closestT = dist;
                                bestHit = info;
                                bestHit.chunkId = k;
                                bestHit.pos = hitWorld;
                                bestHit.normal = normalize(mul(info.normal, (float3x3)chunk.worldToLocal));
                            }
                        }
                    }
                }
            }
        }
        else 
        {
           float tCurrent = max(0.0, tScene.x);
            float tMaxLimit = min(closestT, tScene.y);
            float3 cellSize = (_TLASBoundsMax - _TLASBoundsMin) / float(_TLASResolution);
            float3 invDir = 1.0 / (rayDir + sign(rayDir) * 1e-6);
            float3 startPos = rayOrigin + rayDir * (tCurrent + 1e-4) - _TLASBoundsMin;
            int3 cellIdx = clamp(int3(startPos / cellSize), 0, _TLASResolution - 1);
            int3 step = int3(sign(rayDir));
            float3 tDelta = abs(cellSize * invDir);
            float3 cellBoundMin = _TLASBoundsMin + float3(cellIdx) * cellSize;
            float3 cellBoundMax = cellBoundMin + cellSize;
            float3 nextBoundary = float3(
                rayDir.x > 0 ? cellBoundMax.x : cellBoundMin.x, 
                rayDir.y > 0 ? cellBoundMax.y : cellBoundMin.y, 
                rayDir.z > 0 ? cellBoundMax.z : cellBoundMin.z
            );
            float3 tNext = (nextBoundary - rayOrigin) * invDir;

            int iter = 0;
            int maxIter = _TLASResolution * 3;
            while (iter < maxIter && tCurrent < tMaxLimit)
            {
                if (all(cellIdx >= 0) && all(cellIdx < _TLASResolution))
                {
                    uint flatIdx = cellIdx.z * _TLASResolution * _TLASResolution + cellIdx.y * _TLASResolution + cellIdx.x;
                    TLASCell cell = _TLASGridBuffer[flatIdx];
                    
                    for (uint k = 0; k < cell.count; k++)
                    {
                        int chunkId = _TLASChunkIndexBuffer[cell.offset + k];
                        ChunkDef chunk = _ChunkBuffer[chunkId];
                        float2 tBox = IntersectBox(rayOrigin, rayDir, chunk.boundsMin, chunk.boundsMax);
                        if (tBox.x < tBox.y && tBox.x < closestT && tBox.y > tCurrent - 0.01)
                        {
                            if (tBox.y > tCurrent)
                            {
                              float3 localOrigin = mul(chunk.worldToLocal, float4(rayOrigin, 1.0)).xyz;
                              float3 localDir = normalize(mul((float3x3)chunk.worldToLocal, rayDir));
                                float boundsSz = 64.0;
                                float2 tLocalBox = IntersectBox(localOrigin, localDir, float3(0,0,0), float3(boundsSz, boundsSz, boundsSz));
                                if (tLocalBox.x < tLocalBox.y && tLocalBox.y > 0)
                                {
                                    float tStartL = max(0.0, tLocalBox.x);
                                    float tEndL = tLocalBox.y;
                                    HitInfo info = TraceSVO(chunk, localOrigin, localDir, tEndL, tStartL);
                                    if (info.hit)
                                    {
                                        float3 hitWorld = mul(chunk.localToWorld, float4(info.pos, 1.0)).xyz;
                                        float dist = length(hitWorld - rayOrigin);
                                        
                                        if (dist < closestT)
                                        {
                                            closestT = dist;
                                            bestHit = info;
                                            bestHit.chunkId = chunkId;
                                            bestHit.pos = hitWorld;
                                            bestHit.normal = normalize(mul(info.normal, (float3x3)chunk.worldToLocal));
                                        }
                                    }
                                }
                            }
                          }
                    }
                }
                
                float tExit = min(min(tNext.x, tNext.y), tNext.z);
                if (closestT < tExit) break;
                
                tCurrent = tExit;
                
                if (tNext.x <= tNext.y && tNext.x <= tNext.z) 
                { 
                    tNext.x += tDelta.x;
                    cellIdx.x += step.x; 
                }
                else if (tNext.y <= tNext.z) 
                { 
                    tNext.y += tDelta.y;
                    cellIdx.y += step.y; 
                }
                else 
                { 
                    tNext.z += tDelta.z;
                    cellIdx.z += step.z; 
                }
                iter++;
            }
        }
    }
    return bestHit;
}

bool TraceShadow(float3 rayOrigin, float3 rayDir, float maxDist)
{
    float2 tScene = IntersectBox(rayOrigin, rayDir, _TLASBoundsMin, _TLASBoundsMax);
    if (tScene.x >= tScene.y || tScene.y <= 0) return false;
    if (_ChunkCount < 64)
    {
        for (int k = 0; k < _ChunkCount; k++)
        {
            ChunkDef chunk = _ChunkBuffer[k];
            float2 tBox = IntersectBox(rayOrigin, rayDir, chunk.boundsMin, chunk.boundsMax);
            if (tBox.x < tBox.y && tBox.x < maxDist && tBox.y > 0)
            {
                float3 localOrigin = mul(chunk.worldToLocal, float4(rayOrigin, 1.0)).xyz;
                float3 localDir = normalize(mul((float3x3)chunk.worldToLocal, rayDir));
                float boundsSz = 64.0;
                float2 tLocalBox = IntersectBox(localOrigin, localDir, float3(0,0,0), float3(boundsSz, boundsSz, boundsSz));
                if (tLocalBox.x < tLocalBox.y && tLocalBox.y > 0)
                {
                    HitInfo info = TraceSVO(chunk, localOrigin, localDir, min(maxDist, tLocalBox.y), max(0.0, tLocalBox.x));
                    if (info.hit) return true;
                }
            }
        }
    }
    else
    {
        float tCurrent = max(0.0, tScene.x);
        float tMaxLimit = min(maxDist, tScene.y);
        float3 cellSize = (_TLASBoundsMax - _TLASBoundsMin) / float(_TLASResolution);
        float3 invDir = 1.0 / (rayDir + sign(rayDir) * 1e-6);
        float3 startPos = rayOrigin + rayDir * (tCurrent + 1e-4) - _TLASBoundsMin;
        int3 cellIdx = clamp(int3(startPos / cellSize), 0, _TLASResolution - 1);
        int3 step = int3(sign(rayDir));
        float3 tDelta = abs(cellSize * invDir);
        float3 cellBoundMin = _TLASBoundsMin + float3(cellIdx) * cellSize;
        float3 cellBoundMax = cellBoundMin + cellSize;
        float3 nextBoundary = float3(rayDir.x > 0 ? cellBoundMax.x : cellBoundMin.x, rayDir.y > 0 ? cellBoundMax.y : cellBoundMin.y, rayDir.z > 0 ? cellBoundMax.z : cellBoundMin.z);
        float3 tNext = (nextBoundary - rayOrigin) * invDir;

        int iter = 0;
        int maxIter = _TLASResolution * 3;
        while (iter < maxIter && tCurrent < tMaxLimit)
        {
            if (all(cellIdx >= 0) && all(cellIdx < _TLASResolution))
            {
                uint flatIdx = cellIdx.z * _TLASResolution * _TLASResolution + cellIdx.y * _TLASResolution + cellIdx.x;
                TLASCell cell = _TLASGridBuffer[flatIdx];
                for (uint k = 0; k < cell.count; k++)
                {
                    int chunkId = _TLASChunkIndexBuffer[cell.offset + k];
                    ChunkDef chunk = _ChunkBuffer[chunkId];
                    float2 tBox = IntersectBox(rayOrigin, rayDir, chunk.boundsMin, chunk.boundsMax);
                    if (tBox.x < tBox.y && tBox.x < maxDist && tBox.y > tCurrent - 0.01)
                    {
                        float3 localOrigin = mul(chunk.worldToLocal, float4(rayOrigin, 1.0)).xyz;
                        float3 localDir = normalize(mul((float3x3)chunk.worldToLocal, rayDir));
                        float2 tLocalBox = IntersectBox(localOrigin, localDir, float3(0,0,0), float3(64,64,64));
                        if (tLocalBox.x < tLocalBox.y && tLocalBox.y > 0)
                        {
                            HitInfo info = TraceSVO(chunk, localOrigin, localDir, min(maxDist, tLocalBox.y), max(0.0, tLocalBox.x));
                            if (info.hit) return true;
                        }
                    }
                }
            }
            float tExit = min(min(tNext.x, tNext.y), tNext.z);
            tCurrent = tExit;
            if (tNext.x <= tNext.y && tNext.x <= tNext.z) { tNext.x += tDelta.x; cellIdx.x += step.x;
            }
            else if (tNext.y <= tNext.z) { tNext.y += tDelta.y;
            cellIdx.y += step.y; }
            else { tNext.z += tDelta.z;
            cellIdx.z += step.z; }
            iter++;
        }
    }
    return false;
}
