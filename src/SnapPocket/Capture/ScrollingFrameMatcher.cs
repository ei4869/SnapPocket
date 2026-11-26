using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SkiaSharp;

namespace SnapPocket.Capture;

internal sealed class ScrollingFrameMatcher
{
    private const int SamplesPerRow = 32;
    private const int MinTexture = 12;
    private const double MaxScore = 12;
    private const double AmbiguityMargin = 0.35;
    private readonly int _height;
    private readonly int _width;
    private readonly int _edge;
    private readonly int _maxShift;
    private readonly int[] _sampleOffsets = new int[SamplesPerRow];
    private readonly double[] _scores;
    private byte[] _previousRows;
    private byte[] _currentRows;
    private byte[] _previousTexture;
    private byte[] _currentTexture;
    private double _roundingRemainder;
    private double _nextRemainder;

    public ScrollingFrameMatcher(SKBitmap firstFrame, int seamY)
    {
        _width = firstFrame.Width;
        _height = firstFrame.Height;
        _edge = Math.Clamp(_height / 16, 2, 32);
        int minOverlap = Math.Clamp(_height / 10, 8, 48);
        _maxShift = Math.Min(seamY, _height - 2 * _edge - minOverlap);
        _scores = new double[2 * _maxShift + 1];

        _previousRows = new byte[_height * SamplesPerRow];
        _currentRows = new byte[_previousRows.Length];
        _previousTexture = new byte[_height];
        _currentTexture = new byte[_height];
        int left = Math.Min(16, _width / 10);
        int right = _width - Math.Min(26, _width / 8) - 1;
        for (int x = 0; x < SamplesPerRow; x++)
            _sampleOffsets[x] = (left + (right - left) * x / (SamplesPerRow - 1)) * 4;
        Sample(firstFrame, _previousRows, _previousTexture);
    }

    public (int Shift, bool Unchanged) Match(SKBitmap frame)
    {
        if (frame.Width != _width || frame.Height != _height)
            return (0, false);

        Sample(frame, _currentRows, _currentTexture);
        int changedRows = 0;
        for (int y = _edge; y < _height - _edge; y++)
        {
            if (RowDifference(y, y) > SamplesPerRow * 2 && ++changedRows >= 2)
                break;
        }
        if (changedRows < 2)
            return (0, true);

        // Check every pixel offset in both directions. The short sampling grid keeps this
        // independent of the capture width, and checking reverse movement prevents false appends.
        int bestShift = 0;
        double bestScore = ScoreShift(0, 44);
        double zeroScore = bestScore;
        _scores[_maxShift] = bestScore;
        for (int dy = -_maxShift; dy <= _maxShift; dy++)
        {
            if (dy == 0)
                continue;

            double score = ScoreShift(dy, 44);
            _scores[dy + _maxShift] = score;
            if (score < bestScore)
            {
                bestScore = score;
                bestShift = dy;
            }
        }

        if (bestShift == 0 || bestScore >= MaxScore || bestScore >= zeroScore * 0.7)
            return (0, zeroScore < 2);

        // Recheck close candidates with denser sampling before accepting a repeated pattern.
        int coarseShift = bestShift;
        double verifiedBest = ScoreShift(coarseShift, _height);
        _scores[coarseShift + _maxShift] = verifiedBest;
        for (int dy = -_maxShift; dy <= _maxShift; dy++)
        {
            if (dy == coarseShift)
                continue;

            int index = dy + _maxShift;
            if (_scores[index] > bestScore + 4)
            {
                _scores[index] = double.PositiveInfinity;
                continue;
            }
            double score = ScoreShift(dy, _height);
            _scores[index] = score;
            if (score < verifiedBest)
            {
                verifiedBest = score;
                bestShift = dy;
            }
            if (verifiedBest == 0 && score <= AmbiguityMargin && Math.Abs(dy - bestShift) > 1)
                return (0, false);
        }

        if (verifiedBest >= MaxScore)
            return (0, false);
        double threshold = verifiedBest + AmbiguityMargin;
        for (int dy = -_maxShift; dy <= _maxShift; dy++)
            if (Math.Abs(dy - bestShift) > 1 && _scores[dy + _maxShift] <= threshold)
                return (0, false);

        // A compositor can move content by half a physical pixel. Adjacent scores are one
        // possible seam, while distant matches still indicate an ambiguous repeated pattern.
        bool nearLower = bestShift > -_maxShift && _scores[bestShift + _maxShift - 1] <= threshold;
        bool nearUpper = bestShift < _maxShift && _scores[bestShift + _maxShift + 1] <= threshold;
        _nextRemainder = _roundingRemainder;
        if (verifiedBest == 0 || nearLower == nearUpper)
            return (bestShift, false);

        double estimatedShift = bestShift + (nearUpper ? 0.5 : -0.5);
        double adjustedShift = estimatedShift + _roundingRemainder;
        int roundedShift = Math.Clamp((int)Math.Round(adjustedShift), -_maxShift, _maxShift);
        _nextRemainder = adjustedShift - roundedShift;
        return (roundedShift, false);
    }

    public void Accept()
    {
        (_previousRows, _currentRows) = (_currentRows, _previousRows);
        (_previousTexture, _currentTexture) = (_currentTexture, _previousTexture);
        _roundingRemainder = _nextRemainder;
    }

    private double ScoreShift(int shift, int targetRows)
    {
        int start = _edge + Math.Max(0, shift);
        int end = _height - _edge + Math.Min(0, shift);
        int step = Math.Max(1, (end - start) / targetRows);
        int rows = 0;
        int error = 0;
        for (int y = start; y < end; y += step)
        {
            int nextY = y - shift;
            if (Math.Max(_previousTexture[y], _currentTexture[nextY]) < MinTexture)
                continue;

            // Limit the effect of a few changing or fixed rows in an otherwise stable overlap.
            error += Math.Min(36 * SamplesPerRow, RowDifference(y, nextY));
            rows++;
        }
        return rows < 4 ? double.PositiveInfinity : (double)error / (rows * SamplesPerRow);
    }

    private int RowDifference(int previousY, int currentY)
    {
        int p = previousY * SamplesPerRow;
        int n = currentY * SamplesPerRow;
        if (Avx2.IsSupported)
        {
            var previous = Vector256.LoadUnsafe(ref _previousRows[p]);
            var current = Vector256.LoadUnsafe(ref _currentRows[n]);
            return (int)Vector256.Sum(Avx2.SumAbsoluteDifferences(previous, current).AsUInt64());
        }

        int error = 0;
        for (int x = 0; x < SamplesPerRow; x++)
            error += Math.Abs(_previousRows[p + x] - _currentRows[n + x]);
        return error;
    }

    private unsafe void Sample(SKBitmap bitmap, byte[] rows, byte[] texture)
    {
        byte* pixels = (byte*)bitmap.GetPixels();
        for (int y = 0; y < _height; y++)
        {
            byte* line = pixels + y * bitmap.RowBytes;
            int min = 255;
            int max = 0;
            for (int x = 0; x < SamplesPerRow; x++)
            {
                byte* pixel = line + _sampleOffsets[x];
                int value = (pixel[0] + pixel[1] * 2 + pixel[2]) >> 2;
                rows[y * SamplesPerRow + x] = (byte)value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
            texture[y] = (byte)(max - min);
        }

        // Horizontal bands can be distinctive vertically even when a whole row has one color.
        for (int y = 0; y < _height; y++)
        {
            int row = y * SamplesPerRow;
            int above = Math.Max(0, y - 5) * SamplesPerRow;
            int below = Math.Min(_height - 1, y + 5) * SamplesPerRow;
            int verticalChange = 0;
            for (int x = 0; x < SamplesPerRow; x += 8)
                verticalChange += Math.Max(Math.Abs(rows[row + x] - rows[above + x]), Math.Abs(rows[row + x] - rows[below + x]));
            texture[y] = (byte)Math.Max(texture[y], verticalChange / 4);
        }
    }
}
