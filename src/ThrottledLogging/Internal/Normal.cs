namespace ThrottledLogging.Internal;

/// <summary>The standard normal distribution, to the extent the ETA band needs it.</summary>
internal static class Normal
{
    private const double PLow = 0.02425;
    private const double PHigh = 1.0 - PLow;

    private static readonly double[] A = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
    private static readonly double[] B = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
    private static readonly double[] C = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
    private static readonly double[] D = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];

    /// <summary>
    /// The z-score whose two-sided interval covers <paramref name="confidence"/> of the
    /// distribution: 1.28 for 80%, 1.96 for 95%.
    /// </summary>
    /// <param name="confidence">Two-sided confidence, strictly between zero and one.</param>
    /// <returns>The z-score.</returns>
    public static double TwoSidedZ(double confidence) => InverseCdf((1.0 + confidence) / 2.0);

    /// <summary>
    /// Inverse of the standard normal cumulative distribution, by Acklam's rational approximation.
    /// Absolute error stays below 1.15e-9, which is far tighter than an ETA band deserves.
    /// </summary>
    /// <param name="p">A probability, strictly between zero and one.</param>
    /// <returns>The quantile.</returns>
    public static double InverseCdf(double p)
    {
        if (p is <= 0.0 or >= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "Must be strictly between zero and one.");
        }

        if (p < PLow)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((C[0] * q) + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]
                 / ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1.0);
        }

        if (p > PHigh)
        {
            double q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -((((((C[0] * q) + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5])
                 / ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1.0);
        }

        double r = p - 0.5;
        double s = r * r;
        return (((((A[0] * s + A[1]) * s + A[2]) * s + A[3]) * s + A[4]) * s + A[5]) * r
             / (((((B[0] * s + B[1]) * s + B[2]) * s + B[3]) * s + B[4]) * s + 1.0);
    }
}
