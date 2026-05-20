using System;

[Serializable]
public class HeartacheEUISongCue
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
public class HeartacheEUISongCueList
{
    public HeartacheEUISongCue[] cues;
}
