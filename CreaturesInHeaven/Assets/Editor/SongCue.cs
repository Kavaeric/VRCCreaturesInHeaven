using System;

[Serializable]
public class SongCue
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
    public SongCue[] cues;
}
