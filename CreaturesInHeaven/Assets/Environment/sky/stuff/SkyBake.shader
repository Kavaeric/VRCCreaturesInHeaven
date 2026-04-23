Shader "atmospheric/bake"
{
    Properties
    {
        [Header(Atmosphere)] [Space]
        _CrusieHeight ("_CrusieHeight", Range(0, 10000)) = 10000
        _Density ("Atmosphere Density", Range(0, 4)) = 1
        _MieScattering ("Mie Scattering (large particles)", Range(0, 1)) = 1
        _RayleighScattering ("Rayleigh Scattering (small particles)", Range(0, 1)) = 1
        
        _MieScaleHeight ("_MieScaleHeight", Range(0, 1)) = 1
        _RayleighScaleHeight ("_RayleighScaleHeight", Range(0, 1)) = 1
     }

     SubShader
     {
        Lighting Off
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #include "UnityCustomRenderTexture.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.0
            
            sampler2D _BakedTexture;
            float _CrusieHeight;
            float _RayleighScattering;
            float _MieScattering;
            float _Density;
            float _MieScaleHeight;
            float _RayleighScaleHeight;
            #define PI 3.141592
            #define iSteps 32
            #define jSteps 16

            float2 rsi(float3 r0, float3 rd, float sr)
            {
                // ray-sphere intersection that assumes
                // the sphere is centered at the origin.
                // No intersection when result.x > result.y
                float a = dot(rd, rd);
                float b = 2.0 * dot(rd, r0);
                float c = dot(r0, r0) - (sr * sr);
                float d = (b*b) - 4.0*a*c;
                if (d < 0.0) return float2(1e5,-1e5);
                return float2(
                    (-b - sqrt(d))/(2.0*a),
                    (-b + sqrt(d))/(2.0*a)
                );
            }

            float3 atmosphere(float3 r, float3 r0, float3 pSun, float iSun, float rPlanet, float rAtmos, 
                float3 kRlh, float kMie, float shRlh, float shMie, float g)
            {
                // Normalize the sun and view directions.
                pSun = normalize(pSun);
                r = normalize(r);

                // Calculate the step size of the primary ray.
                float2 p = rsi(r0, r, rAtmos);
                if (p.x > p.y) return float3(0,0,0);
#if 0
                p.y = min(p.y, rsi(r0, r, rPlanet).x);
#else
                //float planetHit = rsi(r0, r, rPlanet).x;
                //if (planetHit > 0.0)
                //    p.y = min(p.y, planetHit);
                //p.x = max(p.x, 0.0);


                float2 pPlanet = rsi(r0, r, rPlanet);
                if (pPlanet.x <= pPlanet.y && pPlanet.x > 0.0)
                {
                    p.y = min(p.y, pPlanet.x);
                }
                else
                {
                    // Miss: cap to horizon distance so the path length matches
                    // a grazing hit at the horizon boundary.
                    float horizonDist = sqrt(max(dot(r0, r0) - rPlanet * rPlanet, 0.0));
                    p.y = min(p.y, horizonDist);
                }

                p.x = max(p.x, 0.0);

#endif
                float iStepSize = (p.y - p.x) / float(iSteps);

                // Initialize the primary ray time.
                float iTime = 0.0;

                // Initialize accumulators for Rayleigh and Mie scattering.
                float3 totalRlh = float3(0,0,0);
                float3 totalMie = float3(0,0,0);

                // Initialize optical depth accumulators for the primary ray.
                float iOdRlh = 0.0;
                float iOdMie = 0.0;

                // Calculate the Rayleigh and Mie phases.
                float mu = dot(r, pSun);
                float mumu = mu * mu;
                float gg = g * g;
                float pRlh = 3.0 / (16.0 * PI) * (1.0 + mumu);
                float pMie = 3.0 / (8.0 * PI) * ((1.0 - gg) * (mumu + 1.0)) / (pow(1.0 + gg - 2.0 * mu * g, 1.5) * (2.0 + gg));

                // Sample the primary ray.
                for (int i = 0; i < iSteps; i++)
                {
                    // Calculate the primary ray sample position.
                    float3 iPos = r0 + r * (iTime + iStepSize * 0.5);

                    // Calculate the height of the sample.
                    float iHeight = length(iPos) - rPlanet;

                    // Calculate the optical depth of the Rayleigh and Mie scattering for this step.
                    float odStepRlh = exp(-iHeight / shRlh) * iStepSize;
                    float odStepMie = exp(-iHeight / shMie) * iStepSize;

                    // Accumulate optical depth.
                    iOdRlh += odStepRlh;
                    iOdMie += odStepMie;

                    // Calculate the step size of the secondary ray.
                    float jStepSize = rsi(iPos, pSun, rAtmos).y / float(jSteps);

                    // Initialize the secondary ray time.
                    float jTime = 0.0;

                    // Initialize optical depth accumulators for the secondary ray.
                    float jOdRlh = 0.0;
                    float jOdMie = 0.0;

                    // Sample the secondary ray.
                    for (int j = 0; j < jSteps; j++) {

                        // Calculate the secondary ray sample position.
                        float3 jPos = iPos + pSun * (jTime + jStepSize * 0.5);

                        // Calculate the height of the sample.
                        float jHeight = length(jPos) - rPlanet;

                        // Accumulate the optical depth.
                        jOdRlh += exp(-jHeight / shRlh) * jStepSize;
                        jOdMie += exp(-jHeight / shMie) * jStepSize;

                        // Increment the secondary ray time.
                        jTime += jStepSize;
                    }

                    // Calculate attenuation.
                    float3 attn = exp(-(kMie * (iOdMie + jOdMie) + kRlh * (iOdRlh + jOdRlh)));

                    // Accumulate scattering.
                    totalRlh += odStepRlh * attn;
                    totalMie += odStepMie * attn;

                    // Increment the primary ray time.
                    iTime += iStepSize;

                }
                
                float3 transmittance = exp(-(kMie * iOdMie + kRlh * iOdRlh));

                // Calculate and return the final color.
                return iSun * (pRlh * kRlh * totalRlh + pMie * kMie * totalMie);
            }

            float3 LatLongToDir(float2 uv, float bias)
            {
                float lon = (uv.x - 0.5) * (2.0 * PI);
                float t   = 2.0 * uv.y - 1.0;
                float y   = sign(t) * pow(abs(t), bias);
                float r   = sqrt(saturate(1.0 - y * y));  // cos(lat)
                return float3(r * cos(lon), y, r * sin(lon));
            }

            float4 frag (v2f_customrendertexture input) : SV_Target
            {
                float planetRadius = 6371000;
                float atmosphereRadius = 1000000;
                float3 sunDirection = _WorldSpaceLightPos0.xyz;
                float3 rayOrigin = float3(0, planetRadius + _CrusieHeight, 0) + _WorldSpaceCameraPos;

                float3 rayDir = LatLongToDir(input.localTexcoord, 3);

                float3 col = atmosphere(
                    rayDir,                                                     // normalized ray direction
                    rayOrigin,                                                  // ray origin
                    sunDirection,                                                // position of the sun
                    22.0,                                                       // intensity of the sun
                    planetRadius,                                               // radius of the planet in meters
                    planetRadius+atmosphereRadius,                             // radius of the atmosphere in meters
                    float3(5.5e-6, 13.0e-6, 22.4e-6) * _RayleighScattering * _Density,     // Rayleigh scattering coefficient
                    21e-6 * _MieScattering * _Density,                                     // Mie scattering coefficient
                    8e3 * _RayleighScaleHeight,                                                        // Rayleigh scale height
                    1.2e3 * _MieScaleHeight,                                                      // Mie scale height
                    0.758                                                      // Mie preferred scattering direction
                );

                return float4(col, _CrusieHeight);
            }
            ENDCG
        }
    }
}
