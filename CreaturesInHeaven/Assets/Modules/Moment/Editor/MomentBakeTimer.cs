using System.Diagnostics;

// Measures wall-clock time for a single Bakery render within a Moment ALV bake session.
// Call Start() just before RenderButton fires; call StopAndGetSeconds() in OnBakeFinished.
#if BAKERY_INCLUDED
public class MomentBakeTimer
{
    readonly Stopwatch _sw = new();

    public void Start()
    {
        _sw.Restart();
    }

    // Stops the timer and returns elapsed wall-clock seconds, or -1 if Start was never called.
    public float StopAndGetSeconds()
    {
        if (!_sw.IsRunning) return -1f;
        _sw.Stop();
        return (float)_sw.Elapsed.TotalSeconds;
    }
}
#endif
