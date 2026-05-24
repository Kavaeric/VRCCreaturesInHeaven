using System.Collections.Generic;

// Rolling-window estimator for remaining bake time within a single bake session.
// Each completed snapshot's wall-clock render time is fed in via RecordSample;
// the average of the last WindowSize samples is used to project the remaining queue.
//
// History from prior bake sessions (sidecar renderSeconds) is deliberately not used.
// Bake time depends heavily on current scene complexity, Bakery settings, and machine
// load, so a fresh per-session estimate adapts to reality rather than averaging across
// out-of-date conditions.
#if BAKERY_INCLUDED
public class MomentBakeEstimator
{
    const int WindowSize = 8;

    readonly Queue<float> _samples = new();
    float _sum;

    // Average wall-clock seconds across the current window, or -1 if no samples yet.
    public float AverageSeconds => _samples.Count > 0 ? _sum / _samples.Count : -1f;

    public int SampleCount => _samples.Count;

    public void Reset()
    {
        _samples.Clear();
        _sum = 0f;
    }

    // Adds a completed snapshot's render time to the window, dropping the oldest if full.
    // Negative values (the "not recorded" sentinel from MomentBakeTimer) are ignored.
    public void RecordSample(float seconds)
    {
        if (seconds < 0f) return;
        _samples.Enqueue(seconds);
        _sum += seconds;
        if (_samples.Count > WindowSize)
            _sum -= _samples.Dequeue();
    }

    // Estimates remaining wall-clock seconds for the given snapshot count, or -1 if
    // there are no samples yet to base the estimate on.
    public float EstimateRemaining(int snapshotsLeft)
    {
        if (_samples.Count == 0 || snapshotsLeft <= 0) return -1f;
        return AverageSeconds * snapshotsLeft;
    }
}
#endif
