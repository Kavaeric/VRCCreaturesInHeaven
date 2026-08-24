
#if UNITY_EDITOR

// HeadsetSpecs
// Headset display data for the Asset Resolution tools.

// The headsets we have specs for. "Custom" falls back to user-entered values.
public enum HeadsetPreset
{
    ValveIndex,
    SteamFrame,
    Quest3S,
    Quest3,
    QuestPro,
    Beyond2E,
    GalaxyXR,
    Custom
}

// Per-eye display resolution and field of view for one headset.
public struct HeadsetSpec
{
    public int resX, resY;
    public float fovH, fovV;
}

public static class HeadsetSpecs
{
    // Per-eye panel resolution and FOV, in pixels and degrees.
    // Custom is not in this table; callers supply those values themselves.
    public static HeadsetSpec Get(HeadsetPreset preset)
    {
        switch (preset)
        {
            case HeadsetPreset.ValveIndex: return new HeadsetSpec { resX = 1440, resY = 1600, fovH = 108f, fovV = 104f   };
            case HeadsetPreset.SteamFrame: return new HeadsetSpec { resX = 2160, resY = 2160, fovH = 110f, fovV = 110f   };
            case HeadsetPreset.Quest3S:    return new HeadsetSpec { resX = 1832, resY = 1920, fovH = 97f,  fovV = 93f    };
            case HeadsetPreset.Quest3:     return new HeadsetSpec { resX = 2064, resY = 2208, fovH = 104f, fovV = 96.4f  };
            case HeadsetPreset.QuestPro:   return new HeadsetSpec { resX = 1800, resY = 1920, fovH = 106f, fovV = 95.57f };
            case HeadsetPreset.Beyond2E:   return new HeadsetSpec { resX = 2560, resY = 2560, fovH = 110f, fovV = 97f    };
            case HeadsetPreset.GalaxyXR:   return new HeadsetSpec { resX = 3552, resY = 3840, fovH = 108f, fovV = 100f   };
            default:                       return new HeadsetSpec { resX = 1440, resY = 1600, fovH = 108f, fovV = 104f   };
        }
    }

    // Resolves a preset to a spec, substituting the supplied custom values when the
    // preset is Custom. This is the entry point both tools use.
    public static HeadsetSpec Get(HeadsetPreset preset, int customResX, int customResY, float customFovH, float customFovV)
    {
        if (preset == HeadsetPreset.Custom)
            return new HeadsetSpec { resX = customResX, resY = customResY, fovH = customFovH, fovV = customFovV };
        return Get(preset);
    }
}

#endif
