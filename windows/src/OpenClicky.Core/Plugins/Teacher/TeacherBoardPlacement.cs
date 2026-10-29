//
//  TeacherBoardPlacement.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherBoardPlacement.swift)
//
//  Where a lesson is drawn: the largest empty stretch of the screen the student is looking at
//  (the white margins beside a Google results page), found on the screenshot the question already
//  carries. The screen is cut into 16 DIP cells; a cell is empty when its pixels barely vary; the
//  largest rectangle of empty cells that holds the board's shape wins. A plain light area gets ink
//  straight on the page, a plain dark one chalk, and a screen with no room gets a sheet of paper
//  over its quietest side.
//
//  Windows: the maths runs in the monitor's DIPs with the origin at its top-left (y down, so no
//  flip from the screenshot's rows), and the result is turned into physical pixels at the end. The
//  screenshot is decoded into greys by the WPF side (ITeacherBoardSurface.GreyPixels); everything
//  here is pure and unit-tested.
//

using OpenClicky.Core.Platform;
using OpenClicky.Core.Pointing;

namespace OpenClicky.Core.Plugins.Teacher;

/// <summary>How the board looks where it lands.</summary>
public enum TeacherBoardStyle
{
    /// <summary>Dark ink straight on a light, empty part of the page.</summary>
    InkOnLightPage,
    /// <summary>Light chalk straight on a dark, empty part of the screen.</summary>
    ChalkOnDarkPage,
    /// <summary>A sheet of paper floating over a busy screen (or holding a picture).</summary>
    Paper,
}

/// <summary>The screen cut into cells: for each, its mean brightness (0…1) and whether it is empty.</summary>
/// <param name="IsEmpty">Row-major, Rows * Columns.</param>
public sealed record TeacherScreenGrid(int Columns, int Rows, IReadOnlyList<bool> IsEmpty, IReadOnlyList<double> Brightness)
{
    public int Index(int column, int row) => row * Columns + column;
}

/// <summary>A rectangle of cells.</summary>
public readonly record struct TeacherCellRect(int Column, int Row, int Width, int Height);

public static class TeacherBoardPlacement
{
    /// <summary>One cell, in DIPs.</summary>
    public const double CellSize = 16;
    /// <summary>A cell whose brightest and darkest pixels differ by less than this (0…1) is empty.</summary>
    public const double EmptyCellContrastLimit = 0.06;
    /// <summary>Smaller than this and the drawing is too small to read; the paper sheet is used instead.</summary>
    public const double MinimumBoardWidth = 340;
    public const double MaximumBoardWidth = 620;
    /// <summary>Kept clear at the top: the island.</summary>
    public const double TopBand = 64;
    /// <summary>The screenshot is drawn this many grey pixels per cell side (fewer would lose captions).</summary>
    public const int PixelsPerCell = 8;

    /// <summary>Where the board goes on one monitor.</summary>
    /// <param name="FrameInDips">In the monitor's DIPs, from its top-left corner.</param>
    public sealed record Placement(ScreenRect FrameInDips, TeacherBoardStyle Style)
    {
        /// <summary>The frame in physical virtual-desktop pixels, on the monitor at <paramref name="displayFrame"/>.</summary>
        public PixelRect FrameInPixels(PixelRect displayFrame, double scale) => PixelRect.FromEdges(
            displayFrame.Left + (int)Math.Round(FrameInDips.Left * scale),
            displayFrame.Top + (int)Math.Round(FrameInDips.Top * scale),
            displayFrame.Left + (int)Math.Round(FrameInDips.Right * scale),
            displayFrame.Top + (int)Math.Round(FrameInDips.Bottom * scale));
    }

    /// <summary>The cells a monitor of this size in DIPs is cut into.</summary>
    public static (int Columns, int Rows) GridSize(double displayWidth, double displayHeight) =>
        ((int)(displayWidth / CellSize), (int)(displayHeight / CellSize));

    /// <summary>
    /// The board for a sketch (or a picture) of <paramref name="aspectRatio"/> (width / height) on a monitor of
    /// <paramref name="displayWidth"/> × <paramref name="displayHeight"/> DIPs. Without a grid (no usable
    /// screenshot) it falls back to the paper sheet on the side away from the mouse.
    /// </summary>
    /// <param name="mouseX">The mouse in the monitor's DIPs, from its left edge.</param>
    public static Placement Place(double aspectRatio, TeacherScreenGrid? grid, double displayWidth, double displayHeight, double mouseX, bool forcePaper = false)
    {
        var safeAspect = Math.Min(Math.Max(aspectRatio, 0.4), 3);
        var topRowsToSkip = (int)Math.Ceiling(TopBand / CellSize);
        if (forcePaper || grid is null || LargestEmptyRectangle(grid, safeAspect, topRowsToSkip) is not { } best)
        {
            return PaperFallback(safeAspect, displayWidth, displayHeight, mouseX);
        }
        // The board inside the empty rectangle: its shape, one cell of breathing room each side,
        // centred, and no bigger than reads comfortably.
        var availableWidth = (best.Width - 2) * CellSize;
        var availableHeight = (best.Height - 2) * CellSize;
        var boardWidth = Math.Min(Math.Min(availableWidth, availableHeight * safeAspect), MaximumBoardWidth);
        if (boardWidth < MinimumBoardWidth) return PaperFallback(safeAspect, displayWidth, displayHeight, mouseX);
        var boardHeight = Math.Round(boardWidth / safeAspect);
        boardWidth = Math.Round(boardWidth);
        var rectangleMidX = (best.Column + best.Width / 2.0) * CellSize;
        var rectangleMidY = (best.Row + best.Height / 2.0) * CellSize;
        var frame = new ScreenRect(rectangleMidX - boardWidth / 2, rectangleMidY - boardHeight / 2, boardWidth, boardHeight);
        var brightness = MeanBrightness(best, grid);
        var style = brightness >= 0.6 ? TeacherBoardStyle.InkOnLightPage : brightness <= 0.3 ? TeacherBoardStyle.ChalkOnDarkPage : TeacherBoardStyle.Paper;
        return new Placement(frame, style);
    }

    /// <summary>The sheet of paper: about a third of the monitor, on the half away from the mouse, under the top band.</summary>
    public static Placement PaperFallback(double aspectRatio, double displayWidth, double displayHeight, double mouseX)
    {
        var safeAspect = Math.Min(Math.Max(aspectRatio, 0.4), 3);
        var width = Math.Min(displayWidth * 0.36, MaximumBoardWidth);
        var height = width / safeAspect;
        var maximumHeight = displayHeight - TopBand - 48;
        if (height > maximumHeight)
        {
            height = maximumHeight;
            width = height * safeAspect;
        }
        const double margin = 32;
        var mouseIsOnTheRight = mouseX > displayWidth / 2;
        var x = mouseIsOnTheRight ? margin : displayWidth - margin - width;
        var usableMidY = TopBand + (displayHeight - TopBand) / 2;
        return new Placement(new ScreenRect(Math.Round(x), Math.Round(usableMidY - height / 2), Math.Round(width), Math.Round(height)), TeacherBoardStyle.Paper);
    }

    /// <summary>The empty rectangle that holds the biggest board of <paramref name="aspectRatio"/> (cells are square), found
    /// with the histogram method: every maximal empty rectangle is seen once per row it ends on.</summary>
    public static TeacherCellRect? LargestEmptyRectangle(TeacherScreenGrid grid, double aspectRatio, int skippingTopRows = 0)
    {
        if (grid.Columns <= 0 || grid.Rows <= 0) return null;
        var heights = new int[grid.Columns];
        TeacherCellRect? best = null;
        var bestBoardWidth = 0.0;

        void Consider(int column, int width, int height, int bottomRow)
        {
            if (width < 3 || height < 3) return;
            // Inside a margin of one cell each side, the widest board of this shape that fits.
            var boardWidth = Math.Min(width - 2, (height - 2) * aspectRatio);
            // Between two that hold the same board, the roomier one: the board sits in its middle
            // with more white around it.
            var isRoomierTie = boardWidth == bestBoardWidth && width * height > (best is { } current ? current.Width * current.Height : 0);
            if (boardWidth > bestBoardWidth || isRoomierTie)
            {
                bestBoardWidth = boardWidth;
                best = new TeacherCellRect(column, bottomRow - height + 1, width, height);
            }
        }

        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                var empty = row >= skippingTopRows && grid.IsEmpty[grid.Index(column, row)];
                heights[column] = empty ? heights[column] + 1 : 0;
            }
            // Largest rectangles under the histogram of `heights`.
            var stack = new Stack<int>();
            for (var column = 0; column <= grid.Columns; column++)
            {
                var currentHeight = column == grid.Columns ? 0 : heights[column];
                while (stack.Count > 0 && heights[stack.Peek()] >= currentHeight)
                {
                    var top = stack.Pop();
                    var height = heights[top];
                    var leftEdge = (stack.Count > 0 ? stack.Peek() : -1) + 1;
                    var width = column - leftEdge;
                    if (height > 0) Consider(leftEdge, width, height, row);
                }
                stack.Push(column);
            }
        }
        return best;
    }

    public static double MeanBrightness(TeacherCellRect rectangle, TeacherScreenGrid grid)
    {
        var total = 0.0;
        var count = 0;
        for (var row = rectangle.Row; row < rectangle.Row + rectangle.Height; row++)
        {
            for (var column = rectangle.Column; column < rectangle.Column + rectangle.Width; column++)
            {
                total += grid.Brightness[grid.Index(column, row)];
                count++;
            }
        }
        return count == 0 ? 0 : total / count;
    }

    /// <summary>Each cell's contrast and brightness. <paramref name="pixels"/> row 0 is the top of the screen.</summary>
    public static TeacherScreenGrid GridFromGreyPixels(IReadOnlyList<byte> pixels, int width, int height, int pixelsPerCell)
    {
        var columns = width / pixelsPerCell;
        var rows = height / pixelsPerCell;
        var isEmpty = new bool[columns * rows];
        var brightness = new double[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                byte darkest = 255, brightest = 0;
                var total = 0;
                for (var y = row * pixelsPerCell; y < (row + 1) * pixelsPerCell; y++)
                {
                    for (var x = column * pixelsPerCell; x < (column + 1) * pixelsPerCell; x++)
                    {
                        var value = pixels[y * width + x];
                        darkest = Math.Min(darkest, value);
                        brightest = Math.Max(brightest, value);
                        total += value;
                    }
                }
                var index = row * columns + column;
                isEmpty[index] = (brightest - darkest) / 255.0 < EmptyCellContrastLimit;
                brightness[index] = total / (double)(pixelsPerCell * pixelsPerCell) / 255;
            }
        }
        return new TeacherScreenGrid(columns, rows, isEmpty, brightness);
    }
}
