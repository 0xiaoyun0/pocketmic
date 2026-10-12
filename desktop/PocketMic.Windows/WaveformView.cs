using System.Windows;
using System.Windows.Media;

namespace PocketMic.Windows;

public sealed class WaveformView : FrameworkElement
{
    private readonly float[] history = new float[72];
    public void Push(float peak)
    {
        Array.Copy(history, 1, history, 0, history.Length - 1);
        history[^1] = peak;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(52, 72, 61)), 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(gridPen, new Point(0, ActualHeight * i / 4), new Point(ActualWidth, ActualHeight * i / 4));
        double step = ActualWidth / history.Length;
        for (int i = 0; i < history.Length; i++)
        {
            double height = Math.Max(3, Math.Min(1, history[i]) * ActualHeight * 0.88);
            var brush = new SolidColorBrush(history[i] >= .96 ? Color.FromRgb(245, 182, 109) : Color.FromRgb(186, 227, 143));
            dc.DrawRoundedRectangle(brush, null, new Rect(i * step, (ActualHeight - height) / 2, Math.Max(2, step - 3), height), 2, 2);
        }
    }
}
