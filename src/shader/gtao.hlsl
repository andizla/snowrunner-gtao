// SPDX-License-Identifier: GPL-3.0-only
// SnowRunner GTAO: replacement for the game's two SSAO generation shaders (full and half resolution).
//
// The game's own pass is a depth-only SSAO with 8 samples on a fixed kernel and a radius that covers the same share
// of the screen at any distance. This shader integrates the visible horizon in several screen directions instead
// (ground truth ambient occlusion, Jimenez et al. 2016, "Practical Real-Time Strategies for Accurate Indirect
// Occlusion"), with a radius in metres, a normal rebuilt from depth and interleaved gradient noise that the game's own
// 3x3 blur in the apply pass averages out. The horizon integration follows Intel's XeGTAO (MIT, see
// THIRD_PARTY_NOTICES.md).
//
// Contract with the engine, taken from the original shader: the same constant buffers, textures, sampler array,
// input and output signature, by name and slot. Output x = visibility (1 = open), yzw = the centre depth. The pass
// gets no projection constants, so the field of view is assumed, as the original does with its fixed pixel footprint
// (55.6 degrees vertical).
//
// Build: fxc -T ps_5_0 -E main gtao.hlsl. Every value below can be set with -D NAME=value.

#ifndef AO_SLICES
#define AO_SLICES 4            // screen directions per pixel
#endif
#ifndef AO_STEPS
#define AO_STEPS 8             // depth taps per direction and side
#endif
#ifndef AO_RADIUS
#define AO_RADIUS 1.5          // metres
#endif
#ifndef AO_RADIUS_MAX_DEPTH_FRACTION
#define AO_RADIUS_MAX_DEPTH_FRACTION 0.12   // caps the radius near the camera and keeps the kernel bounded on screen
#endif
#ifndef AO_POWER
#define AO_POWER 1.6           // final exponent on the visibility
#endif
#ifndef AO_FADE_START
#define AO_FADE_START 150.0    // metres: the effect fades out between start and end, fog owns the distance
#endif
#ifndef AO_FADE_END
#define AO_FADE_END 300.0
#endif
#ifndef TAN_HALF_FOV_Y
#define TAN_HALF_FOV_Y 0.5275
#endif
#ifndef DEBUG_STRIPES
#define DEBUG_STRIPES 0        // 1 = vertical stripes instead of AO, shows that this shader is the one running
#endif

cbuffer CB_INSTANCE : register(b4)
{
    float2 g_vDitherTile;
    float2 g_vRadiusMinMax;
    float4 g_vSSAOColor;
};
cbuffer CB_GLOBAL_TARGET : register(b0)
{
    float2 g_vBBSizeInv;
    float2 g_vVPSizeInv;
    uint   g_iBBSampleCount;
};
SamplerState      _SAMPLERS[16] : register(s0);   // the engine's sampler array: 0 = depth, 1 = dither (wrap), 2 = factor
Texture2D<float4> g_txDither : register(t0);
Texture2D<float4> g_txFactor : register(t2);
Texture2D<float4> g_txZ      : register(t80);

static const float PI = 3.14159265;
static const float HALF_PI = 1.57079633;

float Depth(float2 uv) { return g_txZ.SampleLevel(_SAMPLERS[0], uv, 0).x; }

// View space: x right, y up, z forward. uv (0,0) is the top left corner.
float3 ViewPos(float2 uv, float z, float2 tanHalfFov)
{
    return float3((uv.x * 2.0 - 1.0) * tanHalfFov.x, (1.0 - uv.y * 2.0) * tanHalfFov.y, 1.0) * z;
}

float InterleavedGradientNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float4 main(float2 uv : TEXCOORD0, uint frontFace : SV_IsFrontFace) : SV_Target0
{
    float2 px = g_vBBSizeInv;
    float  z  = Depth(uv);
    float fade = saturate((AO_FADE_END - z) / (AO_FADE_END - AO_FADE_START));
    if (fade <= 0.0 || z <= 0.0)
        return float4(1.0, z, z, z);

    float2 tanHalfFov = float2(TAN_HALF_FOV_Y * px.y / px.x, TAN_HALF_FOV_Y);
    float3 P = ViewPos(uv, z, tanHalfFov);

    // Normal from depth: per axis keep the neighbour whose depth is closer to the centre, so edges stay sharp
    float zl = Depth(uv - float2(px.x, 0.0)), zr = Depth(uv + float2(px.x, 0.0));
    float zu = Depth(uv - float2(0.0, px.y)), zd = Depth(uv + float2(0.0, px.y));
    float3 dx = abs(zr - z) < abs(z - zl) ? ViewPos(uv + float2(px.x, 0.0), zr, tanHalfFov) - P : P - ViewPos(uv - float2(px.x, 0.0), zl, tanHalfFov);
    float3 dy = abs(zd - z) < abs(z - zu) ? ViewPos(uv + float2(0.0, px.y), zd, tanHalfFov) - P : P - ViewPos(uv - float2(0.0, px.y), zu, tanHalfFov);
    float3 N = normalize(cross(dx, dy));   // dx points right, dy points down the screen: the cross product faces the camera
    float3 V = normalize(-P);

    // Radius: the game's min/max radius and its per pixel factor keep their meaning as a scale on the metric radius
    float factor  = saturate(g_txFactor.SampleLevel(_SAMPLERS[2], uv, 0).x * 2.0 - 1.0);
    float rScale  = lerp(g_vRadiusMinMax.x, g_vRadiusMinMax.y, factor) / max(g_vRadiusMinMax.y, 1e-4);
    float radius  = min(AO_RADIUS * rScale, z * AO_RADIUS_MAX_DEPTH_FRACTION);
    float radiusPx = radius / (z * 2.0 * tanHalfFov.y * px.y);   // one pixel covers z * 2 tan(fov/2) / height
    if (radiusPx < 1.5)
        return float4(1.0, z, z, z);

    float falloffRange = 0.615 * radius;
    float falloffMul   = -1.0 / falloffRange;
    float falloffAdd   = (radius - falloffRange) / falloffRange + 1.0;

    float2 pixel = uv / px;
    // The dither texture only nudges the noise. It stays referenced so the shader declares every resource and constant
    // the original declares: the engine sees this blob alone and may bind by those names.
    float ditherNudge = g_txDither.SampleLevel(_SAMPLERS[1], uv * g_vDitherTile, 0).x * (1.0 / 1024.0);
    float noiseSlice  = frac(InterleavedGradientNoise(pixel) + ditherNudge);
    float noiseSample = InterleavedGradientNoise(pixel + float2(17.0, 43.0));

    float visibility = 0.0;
    for (int slice = 0; slice < AO_SLICES; slice++)
    {
        float  phi = (slice + noiseSlice) / AO_SLICES * PI;
        float2 dir = float2(cos(phi), sin(phi));
        float2 omega = float2(dir.x, -dir.y) * radiusPx * px;   // screen y runs down

        float3 directionVec = float3(dir.x, dir.y, 0.0);
        float3 orthoDirection = directionVec - dot(directionVec, V) * V;
        float3 axis = normalize(cross(orthoDirection, V));
        float3 projN = N - axis * dot(N, axis);
        float  projLen = length(projN);
        float  signN = sign(dot(orthoDirection, projN));
        float  cosN = saturate(dot(projN, V) / max(projLen, 1e-5));
        float  n = signN * acos(cosN);

        float lowHorizon0 = cos(n + HALF_PI), lowHorizon1 = cos(n - HALF_PI);
        float horizon0 = lowHorizon0, horizon1 = lowHorizon1;
        for (int step = 0; step < AO_STEPS; step++)
        {
            float s = (step + frac(noiseSample + step * 0.6180339887)) / AO_STEPS;
            s = s * s + 1.3 / radiusPx;   // denser near the centre, never closer than about a pixel
            float2 offset = s * omega;

            float2 uv0 = (floor((uv + offset) / px) + 0.5) * px;
            float3 d0 = ViewPos(uv0, Depth(uv0), tanHalfFov) - P;
            float  l0 = length(d0);
            float  c0 = lerp(lowHorizon0, dot(d0 / max(l0, 1e-5), V), saturate(l0 * falloffMul + falloffAdd));
            horizon0 = max(horizon0, c0);

            float2 uv1 = (floor((uv - offset) / px) + 0.5) * px;
            float3 d1 = ViewPos(uv1, Depth(uv1), tanHalfFov) - P;
            float  l1 = length(d1);
            float  c1 = lerp(lowHorizon1, dot(d1 / max(l1, 1e-5), V), saturate(l1 * falloffMul + falloffAdd));
            horizon1 = max(horizon1, c1);
        }

        projLen = lerp(projLen, 1.0, 0.05);
        float h0 = -acos(clamp(horizon1, -1.0, 1.0));
        float h1 =  acos(clamp(horizon0, -1.0, 1.0));
        h0 = n + clamp(h0 - n, -HALF_PI, HALF_PI);
        h1 = n + clamp(h1 - n, -HALF_PI, HALF_PI);
        float arc0 = (cosN + 2.0 * h0 * sin(n) - cos(2.0 * h0 - n)) * 0.25;
        float arc1 = (cosN + 2.0 * h1 * sin(n) - cos(2.0 * h1 - n)) * 0.25;
        visibility += projLen * (arc0 + arc1);
    }
    visibility = pow(saturate(visibility / AO_SLICES), AO_POWER);
    visibility = lerp(1.0, visibility, fade);
#if DEBUG_STRIPES
    // The real result keeps a 1/1024 share, so this build declares the same resources.
    visibility = lerp(frac(uv.x * 20.0) < 0.5 ? 0.0 : 1.0, visibility, 1.0 / 1024.0);
#endif
    return float4(visibility, z, z, z);
}
