using System;

[Serializable]
public class HeartacheSongCue
{
    public int measure;
    public int beat;
    public int tick;
    public float timeSeconds;
    public string marker;
    public string section;
    public string lyric;
}

[Serializable]
public class HeartacheSongCueList
{
    public HeartacheSongCue[] cues;
}
