namespace Shrike.Core.Recording;

/// <summary>
/// The <b>1€ filter</b> (Casiez, Roussel &amp; Vogel, 2012): an adaptive low-pass for noisy pointer input.
/// It smooths hard when the signal moves slowly (killing hand jitter) and lightly when it moves fast
/// (keeping lag low), by raising its cutoff frequency with the estimated speed — so a smoothed cursor
/// glides when drifting yet still snaps on a quick flick. Scalar; apply one instance per axis. Timestamps
/// are in seconds. Tuning: <paramref name="minCutoff"/> is the baseline smoothing (lower = smoother),
/// <paramref name="beta"/> how quickly it loosens with speed.
/// </summary>
public sealed class OneEuroFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private double _xPrev;   // previous filtered value
    private double _dxPrev;  // previous filtered derivative
    private double _tPrev;   // previous timestamp (seconds)
    private bool _has;

    public OneEuroFilter(double minCutoff, double beta, double dCutoff = 1.0)
    {
        if (minCutoff <= 0) throw new ArgumentOutOfRangeException(nameof(minCutoff));
        if (dCutoff <= 0) throw new ArgumentOutOfRangeException(nameof(dCutoff));
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    /// <summary>Feed the next sample and get the filtered value. Samples must arrive in time order.</summary>
    public double Filter(double value, double timestampSeconds)
    {
        if (!_has)
        {
            _has = true;
            _xPrev = value;
            _dxPrev = 0;
            _tPrev = timestampSeconds;
            return value;
        }

        var dt = timestampSeconds - _tPrev;
        if (dt <= 0) dt = 1e-6; // guard equal/backwards timestamps
        _tPrev = timestampSeconds;

        // Speed estimate: derivative of the (previously filtered) signal, low-passed at the derivative cutoff.
        var dx = (value - _xPrev) / dt;
        var eDx = LowPass(dx, ref _dxPrev, Alpha(_dCutoff, dt));

        // Cutoff rises with speed; low-pass the value at that adaptive cutoff.
        var cutoff = _minCutoff + _beta * Math.Abs(eDx);
        return LowPass(value, ref _xPrev, Alpha(cutoff, dt));
    }

    private static double Alpha(double cutoff, double dt)
    {
        var tau = 1.0 / (2.0 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }

    private static double LowPass(double value, ref double prev, double alpha)
    {
        var y = alpha * value + (1 - alpha) * prev;
        prev = y;
        return y;
    }
}
