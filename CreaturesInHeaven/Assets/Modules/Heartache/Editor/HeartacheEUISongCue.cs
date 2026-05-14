using System;

[Serializable]
public class HeartacheEUISongCue
{
    public string marker;
    public string section;
    public int measure;
    public int beat;
    public int tick;
    public float timeSeconds;
    public string lyric;
}

[Serializable]
public class SongCueList
{
    public HeartacheEUISongCue[] cues;
}
