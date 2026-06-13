/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
#version 330

in vec2 texCoord;
out vec4 outputColor;

uniform sampler2D texture0;
uniform int filterMode;
uniform int upscaleMode;
uniform int upscaleFactor;
uniform vec2 cropOrigin;
uniform vec2 cropSize;
uniform vec2 sourceTextureSize;

int effectiveUpscaleFactor()
{
    return max(1, upscaleFactor);
}

vec2 upscaledSize()
{
    return cropSize * float(effectiveUpscaleFactor());
}

vec2 clampSourcePixel(vec2 pixel)
{
    vec2 minPixel = cropOrigin;
    vec2 maxPixel = cropOrigin + cropSize - vec2(1.0, 1.0);
    return clamp(pixel, minPixel, maxPixel);
}

vec2 clampUpscaledPixel(vec2 pixel)
{
    vec2 minPixel = vec2(0.0, 0.0);
    vec2 maxPixel = upscaledSize() - vec2(1.0, 1.0);
    return clamp(pixel, minPixel, maxPixel);
}

vec4 fetchSourcePixel(vec2 pixel)
{
    vec2 clampedPixel = clampSourcePixel(floor(pixel));
    return texelFetch(texture0, ivec2(clampedPixel), 0);
}

bool sameColor(vec4 a, vec4 b)
{
    return distance(a.rgb, b.rgb) < 0.001 && abs(a.a - b.a) < 0.001;
}

vec2 sourcePixelFromUpscaled(vec2 upscaledPixel)
{
    float factor = float(effectiveUpscaleFactor());
    return cropOrigin + floor(clampUpscaledPixel(upscaledPixel) / factor);
}

vec4 sampleRawUpscaledPixel(vec2 upscaledPixel)
{
    return fetchSourcePixel(sourcePixelFromUpscaled(upscaledPixel));
}

vec4 sampleScale2xPixel(vec2 upscaledPixel)
{
    vec2 clampedPixel = clampUpscaledPixel(floor(upscaledPixel));
    vec2 sourcePixel = cropOrigin + floor(clampedPixel / 2.0);
    vec2 subPixel = mod(clampedPixel, 2.0);

    vec4 b = fetchSourcePixel(sourcePixel + vec2(0.0, -1.0));
    vec4 d = fetchSourcePixel(sourcePixel + vec2(-1.0, 0.0));
    vec4 e = fetchSourcePixel(sourcePixel);
    vec4 f = fetchSourcePixel(sourcePixel + vec2(1.0, 0.0));
    vec4 h = fetchSourcePixel(sourcePixel + vec2(0.0, 1.0));

    if (!sameColor(b, h) && !sameColor(d, f))
    {
        if (subPixel.x < 0.5 && subPixel.y < 0.5)
        {
            return sameColor(d, b) ? d : e;
        }

        if (subPixel.x >= 0.5 && subPixel.y < 0.5)
        {
            return sameColor(b, f) ? f : e;
        }

        if (subPixel.x < 0.5)
        {
            return sameColor(d, h) ? d : e;
        }

        return sameColor(h, f) ? f : e;
    }

    return e;
}

vec4 sampleScale3xPixel(vec2 upscaledPixel)
{
    vec2 clampedPixel = clampUpscaledPixel(floor(upscaledPixel));
    vec2 sourcePixel = cropOrigin + floor(clampedPixel / 3.0);
    vec2 subPixel = mod(clampedPixel, 3.0);

    vec4 a = fetchSourcePixel(sourcePixel + vec2(-1.0, -1.0));
    vec4 b = fetchSourcePixel(sourcePixel + vec2(0.0, -1.0));
    vec4 c = fetchSourcePixel(sourcePixel + vec2(1.0, -1.0));
    vec4 d = fetchSourcePixel(sourcePixel + vec2(-1.0, 0.0));
    vec4 e = fetchSourcePixel(sourcePixel);
    vec4 f = fetchSourcePixel(sourcePixel + vec2(1.0, 0.0));
    vec4 g = fetchSourcePixel(sourcePixel + vec2(-1.0, 1.0));
    vec4 h = fetchSourcePixel(sourcePixel + vec2(0.0, 1.0));
    vec4 i = fetchSourcePixel(sourcePixel + vec2(1.0, 1.0));

    if (sameColor(b, h) || sameColor(d, f))
    {
        return e;
    }

    if (subPixel.x < 0.5 && subPixel.y < 0.5)
    {
        return sameColor(d, b) ? d : e;
    }

    if (subPixel.x > 1.5 && subPixel.y < 0.5)
    {
        return sameColor(b, f) ? f : e;
    }

    if (subPixel.x < 0.5 && subPixel.y > 1.5)
    {
        return sameColor(d, h) ? d : e;
    }

    if (subPixel.x > 1.5 && subPixel.y > 1.5)
    {
        return sameColor(h, f) ? f : e;
    }

    if (subPixel.y < 0.5)
    {
        bool leftCorner = sameColor(d, b) && !sameColor(e, c);
        bool rightCorner = sameColor(b, f) && !sameColor(e, a);
        return (leftCorner || rightCorner) ? b : e;
    }

    if (subPixel.x < 0.5)
    {
        bool topCorner = sameColor(d, b) && !sameColor(e, g);
        bool bottomCorner = sameColor(d, h) && !sameColor(e, a);
        return (topCorner || bottomCorner) ? d : e;
    }

    if (subPixel.x > 1.5)
    {
        bool topCorner = sameColor(b, f) && !sameColor(e, i);
        bool bottomCorner = sameColor(h, f) && !sameColor(e, c);
        return (topCorner || bottomCorner) ? f : e;
    }

    if (subPixel.y > 1.5)
    {
        bool leftCorner = sameColor(d, h) && !sameColor(e, i);
        bool rightCorner = sameColor(h, f) && !sameColor(e, g);
        return (leftCorner || rightCorner) ? h : e;
    }

    return e;
}

vec4 blendToward(vec4 baseColor, vec4 blendColor, float weight)
{
    return vec4(mix(baseColor.rgb, blendColor.rgb, clamp(weight, 0.0, 1.0)), baseColor.a);
}

vec4 sampleHqPixel(vec2 upscaledPixel)
{
    float factor = float(effectiveUpscaleFactor());
    vec2 clampedPixel = clampUpscaledPixel(floor(upscaledPixel));
    vec2 sourcePixel = cropOrigin + floor(clampedPixel / factor);
    vec2 local = (mod(clampedPixel, factor) + vec2(0.5, 0.5)) / factor;

    vec4 b = fetchSourcePixel(sourcePixel + vec2(0.0, -1.0));
    vec4 d = fetchSourcePixel(sourcePixel + vec2(-1.0, 0.0));
    vec4 e = fetchSourcePixel(sourcePixel);
    vec4 f = fetchSourcePixel(sourcePixel + vec2(1.0, 0.0));
    vec4 h = fetchSourcePixel(sourcePixel + vec2(0.0, 1.0));

    vec4 result = e;
    float horizontalEdge = distance(d.rgb, f.rgb);
    float verticalEdge = distance(b.rgb, h.rgb);
    float cornerWeight = 0.46;
    float edgeWeight = 0.22;

    if (local.x < 0.5 && local.y < 0.5 && sameColor(b, d) && !sameColor(e, b))
    {
        result = blendToward(result, b, cornerWeight * (1.0 - max(local.x, local.y) * 2.0));
    }
    else if (local.x >= 0.5 && local.y < 0.5 && sameColor(b, f) && !sameColor(e, b))
    {
        result = blendToward(result, b, cornerWeight * (1.0 - max(1.0 - local.x, local.y) * 2.0));
    }
    else if (local.x < 0.5 && local.y >= 0.5 && sameColor(h, d) && !sameColor(e, h))
    {
        result = blendToward(result, h, cornerWeight * (1.0 - max(local.x, 1.0 - local.y) * 2.0));
    }
    else if (local.x >= 0.5 && local.y >= 0.5 && sameColor(h, f) && !sameColor(e, h))
    {
        result = blendToward(result, h, cornerWeight * (1.0 - max(1.0 - local.x, 1.0 - local.y) * 2.0));
    }
    else if (horizontalEdge > verticalEdge * 1.25)
    {
        vec4 side = local.x < 0.5 ? d : f;
        result = blendToward(result, side, edgeWeight * abs(local.x - 0.5) * 2.0);
    }
    else if (verticalEdge > horizontalEdge * 1.25)
    {
        vec4 side = local.y < 0.5 ? b : h;
        result = blendToward(result, side, edgeWeight * abs(local.y - 0.5) * 2.0);
    }

    return result;
}

vec4 sampleUpscaledPixel(vec2 upscaledPixel)
{
    if (upscaleMode == 1 && effectiveUpscaleFactor() == 2)
    {
        return sampleScale2xPixel(upscaledPixel);
    }

    if (upscaleMode == 2 && effectiveUpscaleFactor() == 3)
    {
        return sampleScale3xPixel(upscaledPixel);
    }

    if (upscaleMode >= 3)
    {
        return sampleHqPixel(upscaledPixel);
    }

    return sampleRawUpscaledPixel(upscaledPixel);
}

vec4 fetchUpscaledPixel(vec2 pixel)
{
    return sampleUpscaledPixel(clampUpscaledPixel(floor(pixel)));
}

vec3 blendCrtUpscaledPixel(vec2 pixel)
{
    vec3 center = fetchUpscaledPixel(pixel).rgb;
    vec3 left = fetchUpscaledPixel(pixel + vec2(-1.0, 0.0)).rgb;
    vec3 right = fetchUpscaledPixel(pixel + vec2(1.0, 0.0)).rgb;
    return ((center * 6.0) + left + right) * 0.125;
}

vec3 blendTvUpscaledPixel(vec2 pixel)
{
    vec3 center = fetchUpscaledPixel(pixel).rgb;
    vec3 left = fetchUpscaledPixel(pixel + vec2(-1.0, 0.0)).rgb;
    vec3 right = fetchUpscaledPixel(pixel + vec2(1.0, 0.0)).rgb;
    vec3 up = fetchUpscaledPixel(pixel + vec2(0.0, -1.0)).rgb;
    vec3 down = fetchUpscaledPixel(pixel + vec2(0.0, 1.0)).rgb;
    return ((center * 10.0) + (left * 3.0) + (right * 3.0) + (up * 2.0) + (down * 2.0)) * 0.05;
}

vec3 sampleCrt(vec2 upscaledPixel)
{
    vec2 basePixel = floor(upscaledPixel);
    vec2 fraction = fract(upscaledPixel);
    vec3 topLeft = blendCrtUpscaledPixel(basePixel);
    vec3 topRight = blendCrtUpscaledPixel(basePixel + vec2(1.0, 0.0));
    vec3 bottomLeft = blendCrtUpscaledPixel(basePixel + vec2(0.0, 1.0));
    vec3 bottomRight = blendCrtUpscaledPixel(basePixel + vec2(1.0, 1.0));
    vec3 top = mix(topLeft, topRight, fraction.x);
    vec3 bottom = mix(bottomLeft, bottomRight, fraction.x);
    return mix(topLeft, mix(top, bottom, fraction.y), 48.0 / 256.0);
}

vec3 sampleTv(vec2 upscaledPixel)
{
    vec2 basePixel = floor(upscaledPixel);
    vec2 fraction = fract(upscaledPixel);
    vec3 topLeft = blendTvUpscaledPixel(basePixel);
    vec3 topRight = blendTvUpscaledPixel(basePixel + vec2(1.0, 0.0));
    vec3 bottomLeft = blendTvUpscaledPixel(basePixel + vec2(0.0, 1.0));
    vec3 bottomRight = blendTvUpscaledPixel(basePixel + vec2(1.0, 1.0));
    vec3 top = mix(topLeft, topRight, fraction.x);
    vec3 bottom = mix(bottomLeft, bottomRight, fraction.x);
    return mix(topLeft, mix(top, bottom, fraction.y), 64.0 / 256.0);
}

void main()
{
    vec2 upscaledPixel = texCoord * upscaledSize();

    if (filterMode == 0)
    {
        outputColor = sampleUpscaledPixel(upscaledPixel);
        return;
    }

    vec2 upscaledPixelIndex = clampUpscaledPixel(floor(upscaledPixel));
    vec3 color = filterMode == 1 ? sampleCrt(upscaledPixel) : sampleTv(upscaledPixel);
    float scanline = mod(upscaledPixelIndex.y, 2.0) == 1.0 ? 1.0 : 0.0;
    float multiplier = filterMode == 1
        ? mix(1.02, 0.94, scanline)
        : mix(1.01, 0.93, scanline);
    color *= multiplier;

    if (filterMode == 2)
    {
        float mask = mod(upscaledPixelIndex.x, 3.0);
        if (mask < 0.5)
        {
            color *= vec3(1.03, 0.98, 0.99);
        }
        else if (mask < 1.5)
        {
            color *= vec3(0.99, 1.02, 0.99);
        }
        else
        {
            color *= vec3(0.98, 0.99, 1.03);
        }

        if (mod(upscaledPixelIndex.y, 4.0) == 3.0)
        {
            color *= 0.99;
        }
    }

    outputColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
