using Clip.Shell;

namespace Clip.Tests;

/// <summary>
/// The mini window keeps the video's shape while it is being resized. Getting this wrong is not a
/// crash, it is a window that shudders under the pointer, so the rules are pinned here.
/// </summary>
public class PipAspectTests
{
    private const double Widescreen = 16.0 / 9.0;
    private const int NoMinimum = 1;

    private const int Left = 1;
    private const int Right = 2;
    private const int Top = 3;
    private const int TopLeft = 4;
    private const int TopRight = 5;
    private const int Bottom = 6;
    private const int BottomLeft = 7;
    private const int BottomRight = 8;

    [Theory]
    [InlineData(Left)]
    [InlineData(Right)]
    public void SideEdgesLetWidthLead(int edge)
    {
        var (width, height) = MediaPipWindow.AspectCorrected(edge, 800, 999, Widescreen, NoMinimum);

        Assert.Equal(800, width);
        Assert.Equal(450, height);
    }

    [Theory]
    [InlineData(Top)]
    [InlineData(Bottom)]
    public void TopAndBottomEdgesLetHeightLead(int edge)
    {
        var (width, height) = MediaPipWindow.AspectCorrected(edge, 999, 450, Widescreen, NoMinimum);

        Assert.Equal(800, width);
        Assert.Equal(450, height);
    }

    [Theory]
    [InlineData(TopLeft)]
    [InlineData(TopRight)]
    [InlineData(BottomLeft)]
    [InlineData(BottomRight)]
    public void CornersLeaveAnAlreadyCorrectSizeAlone(int edge)
    {
        var (width, height) = MediaPipWindow.AspectCorrected(edge, 800, 450, Widescreen, NoMinimum);

        Assert.Equal(800, width);
        Assert.Equal(450, height);
    }

    /// <summary>
    /// The bug this replaced: corners took their size from width alone, so dragging one downwards
    /// moved the pointer while the window stayed put and snapped back — the shudder. Vertical
    /// movement has to move the window.
    /// </summary>
    [Fact]
    public void CornersRespondToPurelyVerticalDragging()
    {
        var (_, before) = MediaPipWindow.AspectCorrected(BottomRight, 800, 450, Widescreen, NoMinimum);
        var (_, after) = MediaPipWindow.AspectCorrected(BottomRight, 800, 700, Widescreen, NoMinimum);

        Assert.True(after > before, $"dragging down did not grow the window ({before} -> {after})");
    }

    [Fact]
    public void CornersRespondToPurelyHorizontalDragging()
    {
        var (before, _) = MediaPipWindow.AspectCorrected(BottomRight, 800, 450, Widescreen, NoMinimum);
        var (after, _) = MediaPipWindow.AspectCorrected(BottomRight, 1100, 450, Widescreen, NoMinimum);

        Assert.True(after > before, $"dragging right did not grow the window ({before} -> {after})");
    }

    /// <summary>
    /// Every result has to sit on the aspect line, whichever edge was dragged and however far off
    /// the dragged box was — that is the whole point of the lock.
    /// </summary>
    [Theory]
    [InlineData(Left)]
    [InlineData(Right)]
    [InlineData(Top)]
    [InlineData(Bottom)]
    [InlineData(TopLeft)]
    [InlineData(TopRight)]
    [InlineData(BottomLeft)]
    [InlineData(BottomRight)]
    public void EveryEdgeProducesTheVideosShape(int edge)
    {
        var (width, height) = MediaPipWindow.AspectCorrected(edge, 613, 907, Widescreen, NoMinimum);

        Assert.InRange((double)width / height, Widescreen - 0.01, Widescreen + 0.01);
    }

    /// <summary>
    /// Dragging a corner should land as near the pointer as the aspect permits. Taking width alone
    /// throws the vertical half of the gesture away; the projection keeps both.
    /// </summary>
    [Fact]
    public void CornersLandCloserToThePointerThanWidthAloneWould()
    {
        const double DraggedWidth = 900;
        const double DraggedHeight = 300;

        var (width, height) = MediaPipWindow.AspectCorrected(
            BottomRight, DraggedWidth, DraggedHeight, Widescreen, NoMinimum);

        var chosen = Distance(width, height, DraggedWidth, DraggedHeight);
        var widthLed = Distance(DraggedWidth, DraggedWidth / Widescreen, DraggedWidth, DraggedHeight);

        Assert.True(chosen < widthLed, $"projection was no closer to the pointer ({chosen} vs {widthLed})");
    }

    [Fact]
    public void TheWindowCannotBeDraggedSmallerThanTheMinimum()
    {
        var (width, height) = MediaPipWindow.AspectCorrected(BottomRight, 40, 20, Widescreen, minWidth: 200);

        Assert.Equal(200, width);
        Assert.Equal(113, height);
    }

    [Fact]
    public void TallVideosKeepTheirShapeToo()
    {
        const double Portrait = 9.0 / 16.0;

        var (width, height) = MediaPipWindow.AspectCorrected(BottomRight, 700, 700, Portrait, NoMinimum);

        Assert.InRange((double)width / height, Portrait - 0.01, Portrait + 0.01);
    }

    private static double Distance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));
}
