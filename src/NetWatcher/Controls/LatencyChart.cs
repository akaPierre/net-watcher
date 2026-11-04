using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetWatcher.Models;
using NetWatcher.Services;

namespace NetWatcher.Controls;

/// <summary>
/// Response-time chart for the router and the internet (same unit, one axis).
/// Periods with no internet reply are shaded with the status "critical" color and labelled in the tooltip.
/// Hovering shows a crosshair with the exact values.
/// </summary>
public sealed class LatencyChart : FrameworkElement
{
    // Validated categorical slots 1-2 (blue, orange) + reserved status critical.
    private static readonly Brush InternetBrush = Frozen(Color.FromRgb(0x2A, 0x78, 0xD6));
    private static readonly Brush RouterBrush = Frozen(Color.FromRgb(0xEB, 0x68, 0x34));
    private static readonly Brush OutageFill = Frozen(Color.FromArgb(0x2E, 0xD0, 0x3B, 0x3B));
    private static readonly Brush OutageTick = Frozen(Color.FromRgb(0xD0, 0x3B, 0x3B));
    private static readonly Brush Surface = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush GridBrush = Frozen(Color.FromRgb(0xE8, 0xEB, 0xEE));
    private static readonly Brush AxisText = Frozen(Color.FromRgb(0x6B, 0x77, 0x85));
    private static readonly Brush PrimaryText = Frozen(Color.FromRgb(0x1F, 0x29, 0x33));
    private static readonly Brush CrosshairBrush = Frozen(Color.FromRgb(0x9A, 0xA4, 0xAE));
    private static readonly Brush TooltipBg = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Pen InternetPen = FrozenPen(InternetBrush, 2);
    private static readonly Pen RouterPen = FrozenPen(RouterBrush, 2);
    private static readonly Pen GridPen = FrozenPen(GridBrush, 1);
    private static readonly Pen CrosshairPen = FrozenPen(CrosshairBrush, 1);
    private static readonly Pen RingPen = FrozenPen(Surface, 2);
    private static readonly Pen TooltipBorder = FrozenPen(Frozen(Color.FromRgb(0xDD, 0xE2, 0xE7)), 1);
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private const double PadLeft = 52, PadRight = 120, PadTop = 12, PadBottom = 26;

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<HealthSample>), typeof(LatencyChart),
        new FrameworkPropertyMetadata(Array.Empty<HealthSample>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(
        nameof(Start), typeof(DateTime), typeof(LatencyChart),
        new FrameworkPropertyMetadata(DateTime.Now.AddHours(-24), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndProperty = DependencyProperty.Register(
        nameof(End), typeof(DateTime), typeof(LatencyChart),
        new FrameworkPropertyMetadata(DateTime.Now, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<HealthSample> Samples
    {
        get => (IReadOnlyList<HealthSample>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public DateTime Start { get => (DateTime)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public DateTime End { get => (DateTime)GetValue(EndProperty); set => SetValue(EndProperty, value); }

    private Point? _mouse;

    public LatencyChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Surface, null, new Rect(0, 0, w, h)); // also makes the whole area hit-testable
        var plot = new Rect(PadLeft, PadTop, Math.Max(10, w - PadLeft - PadRight), Math.Max(10, h - PadTop - PadBottom));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var all = Samples ?? [];
        var span = (End - Start).TotalSeconds;
        if (span <= 0) return;

        // ---- downsample to at most one bucket per pixel (keeps spikes and any failure) ----
        var buckets = Downsample(all, plot.Width);

        // ---- y scale: nice max over the larger of the two series, at least 20 ms ----
        double maxVal = buckets.SelectMany(b => new[] { b.Internet, b.Router }).Where(v => v is not null).Select(v => (double)v!).DefaultIfEmpty(0).Max();
        double yMax = NiceCeiling(Math.Max(20, maxVal * 1.1));
        double X(DateTime t) => plot.Left + (t - Start).TotalSeconds / span * plot.Width;
        double Y(double v) => plot.Bottom - Math.Min(v, yMax) / yMax * plot.Height;

        // ---- grid + y labels (recessive) ----
        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4;
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text($"{v:0} ms", 11, AxisText, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        // ---- x labels ----
        foreach (var (t, text) in TimeTicks(Start, End, plot.Width))
        {
            double x = X(t);
            var label = Text(text, 11, AxisText, dpi);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width), plot.Bottom + 6));
        }

        if (all.Count == 0)
        {
            var empty = Text(Loc.T("Health.Collecting"), 13, AxisText, dpi);
            dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        // ---- outage shading: internet without a reply ----
        double expected = ExpectedInterval(all);
        foreach (var b in buckets.Where(b => b.InternetFailed))
        {
            double x0 = X(b.From), x1 = Math.Max(x0 + 2, X(b.To.AddSeconds(expected)));
            dc.DrawRectangle(OutageFill, null, new Rect(x0, plot.Top, Math.Min(x1, plot.Right) - x0, plot.Height));
            dc.DrawRectangle(OutageTick, null, new Rect(x0, plot.Bottom - 3, Math.Min(x1, plot.Right) - x0, 3));
        }

        // ---- lines (break at gaps and at missing replies) ----
        DrawSeries(dc, buckets, b => b.Router, RouterPen, X, Y, expected);
        DrawSeries(dc, buckets, b => b.Internet, InternetPen, X, Y, expected);

        // ---- direct labels at the line ends (text in ink, identity carried by the dot) ----
        // Latest real reading (not the bucket maximum), so it matches the status tiles.
        var lastI = all.LastOrDefault(s => s.InternetMs is not null)?.InternetMs;
        var lastR = all.LastOrDefault(s => s.GatewayMs is not null)?.GatewayMs;
        var endLabels = new List<(double Y, Brush Dot, string Text)>();
        // If the newest check got no reply, say so instead of showing a stale number.
        var newest = all[^1];
        if (newest.InternetMs is null) endLabels.Add((plot.Bottom - 6, InternetBrush, $"{Loc.T("Health.Internet")} {Loc.T("Health.NoReply")}"));
        else if (lastI is { } li) endLabels.Add((Y(li), InternetBrush, $"{Loc.T("Health.Internet")} {li} ms"));
        if (newest.GatewayMs is null) endLabels.Add((plot.Bottom - 6, RouterBrush, $"{Loc.T("Health.Router")} {Loc.T("Health.NoReply")}"));
        else if (lastR is { } lr) endLabels.Add((Y(lr), RouterBrush, $"{Loc.T("Health.Router")} {lr} ms"));
        endLabels.Sort((a, b) => a.Y.CompareTo(b.Y));
        for (int i = 1; i < endLabels.Count; i++) // keep them from overlapping
            if (endLabels[i].Y - endLabels[i - 1].Y < 16) endLabels[i] = endLabels[i] with { Y = endLabels[i - 1].Y + 16 };
        foreach (var (y, dot, text) in endLabels)
        {
            dc.DrawEllipse(dot, null, new Point(plot.Right + 10, y), 4, 4);
            var label = Text(text, 11, PrimaryText, dpi);
            dc.DrawText(label, new Point(plot.Right + 18, y - label.Height / 2));
        }

        // ---- hover: crosshair + markers + tooltip ----
        if (_mouse is { } m && plot.Contains(m) && all.Count > 0)
        {
            var t = Start.AddSeconds((m.X - plot.Left) / plot.Width * span);
            var s = Nearest(all, t);
            if (s is not null)
            {
                double x = Math.Round(X(s.Time)) + 0.5;
                dc.DrawLine(CrosshairPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                if (s.GatewayMs is { } r) dc.DrawEllipse(RouterBrush, RingPen, new Point(x, Y(r)), 4.5, 4.5);
                if (s.InternetMs is { } inet) dc.DrawEllipse(InternetBrush, RingPen, new Point(x, Y(inet)), 4.5, 4.5);

                string noReply = Loc.T("Health.NoReply");
                var lines = new List<(Brush? Dot, FormattedText Text)>
                {
                    (null, Text(s.Time.ToString(span > 86400 ? "g" : "T", CultureInfo.CurrentCulture), 12, PrimaryText, dpi, bold: true)),
                    (InternetBrush, Text($"{Loc.T("Health.Internet")}: {(s.InternetMs is { } a ? $"{a} ms" : noReply)}", 12, PrimaryText, dpi)),
                    (RouterBrush, Text($"{Loc.T("Health.Router")}: {(s.GatewayMs is { } c ? $"{c} ms" : noReply)}", 12, PrimaryText, dpi)),
                };
                if (s.InternetMs is null) lines.Add((OutageTick, Text(Loc.T("Health.OutageTip"), 12, PrimaryText, dpi)));

                double tw = lines.Max(l => l.Text.Width) + 32, th = lines.Sum(l => l.Text.Height + 2) + 14;
                double tx = x + 12 + tw > w ? x - 12 - tw : x + 12;
                double ty = Math.Clamp(m.Y - th / 2, 2, h - th - 2);
                dc.DrawRoundedRectangle(TooltipBg, TooltipBorder, new Rect(tx, ty, tw, th), 6, 6);
                double ly = ty + 7;
                foreach (var (dot, text) in lines)
                {
                    if (dot is not null) dc.DrawEllipse(dot, null, new Point(tx + 14, ly + text.Height / 2), 4, 4);
                    dc.DrawText(text, new Point(tx + (dot is null ? 10 : 24), ly));
                    ly += text.Height + 2;
                }
            }
        }
    }

    private sealed record Bucket(DateTime From, DateTime To, long? Internet, long? Router, bool InternetFailed);

    private List<Bucket> Downsample(IReadOnlyList<HealthSample> samples, double width)
    {
        var result = new List<Bucket>();
        if (samples.Count == 0) return result;
        double span = (End - Start).TotalSeconds;
        double perPixel = span / Math.Max(1, width);
        double expected = ExpectedInterval(samples);
        if (perPixel <= expected * 1.5)
        {
            foreach (var s in samples)
                result.Add(new Bucket(s.Time, s.Time, s.InternetMs, s.GatewayMs, s.InternetMs is null));
            return result;
        }
        foreach (var g in samples.GroupBy(s => (long)((s.Time - Start).TotalSeconds / perPixel)))
        {
            var list = g.ToList();
            result.Add(new Bucket(list[0].Time, list[^1].Time,
                list.Where(s => s.InternetMs is not null).Select(s => s.InternetMs).DefaultIfEmpty(null).Max(),
                list.Where(s => s.GatewayMs is not null).Select(s => s.GatewayMs).DefaultIfEmpty(null).Max(),
                list.Any(s => s.InternetMs is null)));
        }
        return result;
    }

    private static void DrawSeries(DrawingContext dc, List<Bucket> buckets, Func<Bucket, long?> value,
        Pen pen, Func<DateTime, double> x, Func<double, double> y, double expectedSeconds)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool open = false;
            DateTime? prev = null;
            foreach (var b in buckets)
            {
                var v = value(b);
                bool gap = prev is { } p && (b.From - p).TotalSeconds > expectedSeconds * 3;
                if (v is null || gap) open = false;
                if (v is not null)
                {
                    var pt = new Point(x(b.From), y(v.Value));
                    if (!open) { ctx.BeginFigure(pt, false, false); open = true; }
                    else ctx.LineTo(pt, true, true);
                }
                prev = b.To;
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static HealthSample? Nearest(IReadOnlyList<HealthSample> samples, DateTime t)
    {
        HealthSample? best = null;
        double bestD = double.MaxValue;
        foreach (var s in samples)
        {
            double d = Math.Abs((s.Time - t).TotalSeconds);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    private static double ExpectedInterval(IReadOnlyList<HealthSample> samples)
    {
        if (samples.Count < 3) return 60;
        var gaps = new List<double>();
        for (int i = Math.Max(1, samples.Count - 50); i < samples.Count; i++)
            gaps.Add((samples[i].Time - samples[i - 1].Time).TotalSeconds);
        gaps.Sort();
        return Math.Max(5, gaps[gaps.Count / 2]);
    }

    private static IEnumerable<(DateTime, string)> TimeTicks(DateTime start, DateTime end, double width)
    {
        var span = end - start;
        TimeSpan step = span.TotalHours <= 1.5 ? TimeSpan.FromMinutes(10)
            : span.TotalHours <= 7 ? TimeSpan.FromHours(1)
            : span.TotalHours <= 26 ? TimeSpan.FromHours(3)
            : TimeSpan.FromDays(1);
        int maxTicks = Math.Max(2, (int)(width / 70));
        while (span.Ticks / step.Ticks > maxTicks) step += step;
        var first = new DateTime(start.Ticks - start.Ticks % step.Ticks + step.Ticks, start.Kind);
        string fmt = step >= TimeSpan.FromDays(1) ? "ddd dd" : "t";
        for (var t = first; t <= end; t += step)
            yield return (t, t.ToString(fmt, CultureInfo.CurrentCulture));
    }

    private static double NiceCeiling(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, dpi);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Brush b, double width)
    {
        var p = new Pen(b, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }
}
