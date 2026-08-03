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

    public static TheoryData<string, int, int> EveryCorner => new()
    {
        { "top-left", 0, 0 },
        { "top-right", 1920 - 400, 0 },
        { "bottom-left", 0, 1040 - 225 },
        { "bottom-right", 1920 - 400, 1040 - 225 },
        { "top-middle", 760, 0 },
        { "bottom-middle", 760, 1040 - 225 },
        { "middle", 760, 400 },
    };

    /// <summary>
    /// Wherever the window is parked, dragging any of its eight borders outwards has to grow it and
    /// leave it whole and on screen. Against a side there is nowhere to grow into, so it has to
    /// grow towards the middle instead — and it must still grow, not stick.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCorner))]
    public void EveryBorderGrowsTheWindowAndKeepsItOnScreen(string where, int left, int top)
    {
        var start = Box(left, top, left + 400, top + 225);
        var startArea = (long)(start.Right - start.Left) * (start.Bottom - start.Top);

        foreach (var edge in new[] { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight })
        {
            // Push every border 120px outwards, as though dragged away from the window's centre.
            var dragged = Box(
                edge is Left or TopLeft or BottomLeft ? start.Left - 120 : start.Left,
                edge is Top or TopLeft or TopRight ? start.Top - 120 : start.Top,
                edge is Right or TopRight or BottomRight ? start.Right + 120 : start.Right,
                edge is Bottom or BottomLeft or BottomRight ? start.Bottom + 120 : start.Bottom);

            var result = Resize(edge, dragged);
            var area = (long)(result.Right - result.Left) * (result.Bottom - result.Top);

            Assert.True(area > startArea, $"edge {edge} at {where} did not grow the window");
            Assert.True(result.Left >= Screen.Left, $"edge {edge} at {where} ran off the left");
            Assert.True(result.Top >= Screen.Top, $"edge {edge} at {where} ran off the top");
            Assert.True(result.Right <= Screen.Right, $"edge {edge} at {where} ran off the right");
            Assert.True(result.Bottom <= Screen.Bottom, $"edge {edge} at {where} ran off the bottom");
            Assert.InRange(
                (double)(result.Right - result.Left) / (result.Bottom - result.Top),
                Widescreen - 0.02,
                Widescreen + 0.02);
        }
    }

    /// <summary>Shrinking has to work from every border too, and must not shove the window around.</summary>
    [Theory]
    [MemberData(nameof(EveryCorner))]
    public void EveryBorderShrinksTheWindow(string where, int left, int top)
    {
        var start = Box(left, top, left + 800, top + 450);
        var startArea = (long)(start.Right - start.Left) * (start.Bottom - start.Top);

        foreach (var edge in new[] { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight })
        {
            var dragged = Box(
                edge is Left or TopLeft or BottomLeft ? start.Left + 200 : start.Left,
                edge is Top or TopLeft or TopRight ? start.Top + 112 : start.Top,
                edge is Right or TopRight or BottomRight ? start.Right - 200 : start.Right,
                edge is Bottom or BottomLeft or BottomRight ? start.Bottom - 112 : start.Bottom);

            var result = Resize(edge, dragged);
            var area = (long)(result.Right - result.Left) * (result.Bottom - result.Top);

            Assert.True(area < startArea, $"edge {edge} at {where} did not shrink the window");
            Assert.True(result.Left >= Screen.Left && result.Right <= Screen.Right, $"edge {edge} at {where} left the screen");
            Assert.True(result.Top >= Screen.Top && result.Bottom <= Screen.Bottom, $"edge {edge} at {where} left the screen");
        }
    }

    /// <summary>
    /// A screen whose work area does not start at the origin — a second monitor, or one with the
    /// taskbar on the left — must be respected just the same.
    /// </summary>
    [Fact]
    public void AnOffsetScreenIsRespected()
    {
        var second = Box(1920, 0, 3840, 1040);
        var result = MediaPipWindow.ResizedBounds(
            BottomRight, Box(3500, 800, 4300, 1250), second, Widescreen, NoMinimum);

        Assert.True(result.Right <= second.Right, $"ran off the right of the second screen ({result.Right})");
        Assert.True(result.Left >= second.Left, $"ran off the left of the second screen ({result.Left})");
        Assert.True(result.Bottom <= second.Bottom, $"ran off the bottom of the second screen ({result.Bottom})");
    }
}
