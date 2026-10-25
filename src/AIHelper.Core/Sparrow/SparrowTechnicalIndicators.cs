namespace AIHelper.Core.Sparrow;

/// <summary>
/// Pure technical-indicator computations over chronological (oldest-first) series.
/// Every method is deterministic and provider-free; invalid or insufficient input returns
/// false instead of throwing so rule evaluators can surface a DataUnavailable outcome.
/// </summary>
public static class SparrowTechnicalIndicators
{
    /// <summary>
    /// Wilder-smoothed RSI over the most recent gains and losses. Flat history (no gain, no loss)
    /// maps to the neutral 50; a history without losses maps to 100. Requires period + 1 finite
    /// positive closes.
    /// </summary>
    public static bool TryRsi(IReadOnlyList<double> closesOldestFirst, int period, out double rsi)
    {
        rsi = default;
        if (period <= 0 || closesOldestFirst.Count < period + 1 || closesOldestFirst.Any(value => !double.IsFinite(value) || value <= 0))
            return false;

        double avgGain = 0, avgLoss = 0;
        for (int index = 1; index <= period; index++)
        {
            double change = closesOldestFirst[index] - closesOldestFirst[index - 1];
            if (change >= 0) avgGain += change; else avgLoss -= change;
        }
        avgGain /= period;
        avgLoss /= period;

        for (int index = period + 1; index < closesOldestFirst.Count; index++)
        {
            double change = closesOldestFirst[index] - closesOldestFirst[index - 1];
            avgGain = (avgGain * (period - 1) + Math.Max(change, 0)) / period;
            avgLoss = (avgLoss * (period - 1) + Math.Max(-change, 0)) / period;
        }

        if (avgLoss == 0) rsi = avgGain == 0 ? 50 : 100;
        else rsi = 100 - 100 / (1 + avgGain / avgLoss);
        return double.IsFinite(rsi);
    }

    /// <summary>
    /// SMA-seeded EMA MACD. DIF is the fast/slow EMA spread, DEA is the signal EMA of DIF, and the
    /// histogram is the raw DIF - DEA spread (the display-only doubling convention is intentionally
    /// not applied). Requires slow + signal - 1 finite positive closes.
    /// </summary>
    public static bool TryMacd(
        IReadOnlyList<double> closesOldestFirst,
        int fastPeriod,
        int slowPeriod,
        int signalPeriod,
        out double dif,
        out double dea,
        out double histogram)
    {
        dif = dea = histogram = default;
        if (fastPeriod <= 0 || slowPeriod <= fastPeriod || signalPeriod <= 0) return false;
        if (closesOldestFirst.Count < slowPeriod + signalPeriod - 1
            || closesOldestFirst.Any(value => !double.IsFinite(value) || value <= 0))
            return false;

        double[] difSeries = new double[closesOldestFirst.Count - slowPeriod + 1];
        double fastEma = SimpleAverage(closesOldestFirst, 0, fastPeriod);
        double slowEma = SimpleAverage(closesOldestFirst, 0, slowPeriod);
        double fastMultiplier = 2d / (fastPeriod + 1);
        double slowMultiplier = 2d / (slowPeriod + 1);
        for (int index = fastPeriod; index < slowPeriod; index++)
            fastEma += fastMultiplier * (closesOldestFirst[index] - fastEma);
        difSeries[0] = fastEma - slowEma;
        for (int index = slowPeriod; index < closesOldestFirst.Count; index++)
        {
            fastEma += fastMultiplier * (closesOldestFirst[index] - fastEma);
            slowEma += slowMultiplier * (closesOldestFirst[index] - slowEma);
            difSeries[index - slowPeriod + 1] = fastEma - slowEma;
        }

        double signalEma = difSeries.Take(signalPeriod).Average();
        for (int index = signalPeriod; index < difSeries.Length; index++)
            signalEma += (2d / (signalPeriod + 1)) * (difSeries[index] - signalEma);

        dif = difSeries[^1];
        dea = signalEma;
        histogram = dif - dea;
        return double.IsFinite(dif) && double.IsFinite(dea) && double.IsFinite(histogram);
    }

    /// <summary>
    /// Simple mean of true range over the newest period bars. Each true range needs the previous
    /// bar, so period + 1 bars with finite positive high, low, and close are required.
    /// </summary>
    public static bool TryAtr(IReadOnlyList<(double High, double Low, double Close)> barsOldestFirst, int period, out double atr)
    {
        atr = default;
        if (period <= 0 || barsOldestFirst.Count < period + 1) return false;
        if (barsOldestFirst.Any(bar => !double.IsFinite(bar.High) || !double.IsFinite(bar.Low) || !double.IsFinite(bar.Close)
            || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0))
            return false;

        double sum = 0;
        for (int index = barsOldestFirst.Count - period; index < barsOldestFirst.Count; index++)
        {
            (double high, double low, double close) = barsOldestFirst[index];
            double previousClose = barsOldestFirst[index - 1].Close;
            sum += Math.Max(high - low, Math.Max(Math.Abs(high - previousClose), Math.Abs(low - previousClose)));
        }

        atr = sum / period;
        return double.IsFinite(atr) && atr > 0;
    }

    private static double SimpleAverage(IReadOnlyList<double> values, int offset, int count)
    {
        double sum = 0;
        for (int index = offset; index < offset + count; index++) sum += values[index];
        return sum / count;
    }
}
