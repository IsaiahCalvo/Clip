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

    private static MediaPipWindow.RECT Screen => Box(0, 0, 1920, 1040);

    private static MediaPipWindow.RECT Box(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static MediaPipWindow.RECT Resize(int edge, MediaPipWindow.RECT dragged) =>
        MediaPipWindow.ResizedBounds(edge, dragged, Screen, Widescreen, NoMinimum);

    /// <summary>
    /// A window tucked into the bottom-right cannot grow down and right — there is nothing there.
    /// It has to grow towards the middle of the screen instead of hanging off the edge.
    /// </summary>
    [Fact]
    public void GrowingAgainstTheBottomRightPushesTheWindowBackOnScreen()
    {
        var result = Resize(BottomRight, Box(1520, 800, 2320, 1250));

        Assert.True(result.Right <= Screen.Right, $"right edge ran off the screen ({result.Right})");
        Assert.True(result.Bottom <= Screen.Bottom, $"bottom edge ran off the screen ({result.Bottom})");
        Assert.Equal(800, result.Right - result.Left);
    }

    [Fact]
    public void GrowingAgainstTheTopLeftPushesTheWindowBackOnScreen()
    {
        var result = Resize(TopLeft, Box(-400, -220, 400, 230));

        Assert.True(result.Left >= Screen.Left, $"left edge ran off the screen ({result.Left})");
        Assert.True(result.Top >= Screen.Top, $"top edge ran off the screen ({result.Top})");
        Assert.Equal(800, result.Right - result.Left);
    }

    [Fact]
    public void AWindowInTheMiddleIsLeftWhereItWasDragged()
    {
        var result = Resize(BottomRight, Box(600, 400, 1400, 850));

        Assert.Equal(600, result.Left);
        Assert.Equal(400, result.Top);
    }

    [Fact]
    public void TheWindowCannotBeDraggedLargerThanTheScreen()
    {
        var result = Resize(BottomRight, Box(0, 0, 4000, 2250));

        Assert.True(result.Right - result.Left <= Screen.Right - Screen.Left, "wider than the screen");
        Assert.True(result.Bottom - result.Top <= Screen.Bottom - Screen.Top, "taller than the screen");
    }

    /// <summary>A short, wide screen has to cap height, not width, or the window would not fit.</summary>
    [Fact]
    public void AScreenTooShortForTheVideoCapsHeight()
    {
        var shortScreen = Box(0, 0, 4000, 500);
        var result = MediaPipWindow.ResizedBounds(
            BottomRight, Box(0, 0, 3000, 1690), shortScreen, Widescreen, NoMinimum);

        Assert.True(result.Bottom - result.Top <= 500, "taller than the screen");
        Assert.InRange(
            (double)(result.Right - result.Left) / (result.Bottom - result.Top),
            Widescreen - 0.01,
            Widescreen + 0.01);
    }

    [Fact]
    public void TheAnchoredEdgeStillHoldsWhenDraggingLeftwards()
    {
        var result = Resize(Left, Box(600, 400, 1400, 850));

        Assert.Equal(1400, result.Right);
    }
}
