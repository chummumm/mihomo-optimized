using System.Buffers;
using System.Diagnostics;
using System.Numerics;

namespace Mihomo.Desktop.Controls;

/// <summary>Full-resolution, premultiplied sRGB Gaussian convolution. No WPF objects cross this boundary.</summary>
internal static class FluentBackdropPixels
{
    // One shared worker budget also bounds simultaneous menu/dialog/message filters.
    private static readonly SemaphoreSlim work = new(1, 1);
    private sealed record Warmup(bool Completed, double ElapsedMs);
    private static Warmup warmup = new(false, 0);
    private static int warmupStarted;

    internal static void Prewarm()
    {
        if (Interlocked.Exchange(ref warmupStarted, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            long started = Stopwatch.GetTimestamp();
            // Ordinary production startup prepares JIT/SIMD code after the first
            // main-window frame. This contains no application or test fixture data.
            await ApplyAsync(new byte[32 * 32 * 4], 32, 32, 30, 30, 1.5, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref warmup, new Warmup(true, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        });
    }

    internal static object DescribeWarmup()
    {
        var value = Volatile.Read(ref warmup);
        return new { completed = value.Completed, diagnostic = value.Completed ? "available" : "not-completed", elapsedMs = value.ElapsedMs };
    }

    internal static async Task<byte[]> ApplyAsync(byte[] pixels, int width, int height, double sigmaX, double sigmaY, double saturation, CancellationToken cancellation)
    {
        await work.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(() => Apply(pixels, width, height, sigmaX, sigmaY, saturation, cancellation), cancellation).ConfigureAwait(false); }
        finally { work.Release(); }
    }

    internal static byte[] Apply(byte[] pixels, int width, int height, double sigmaX, double sigmaY, double saturation, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || !double.IsFinite(sigmaX) || !double.IsFinite(sigmaY) || sigmaX is < 0 or > 120 || sigmaY is < 0 or > 120 || !double.IsFinite(saturation) || saturation is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid backdrop raster dimensions or filter parameters.");
        int row = checked(width * 4), length = checked(row * height);
        if (pixels.Length < length) throw new ArgumentException("The backdrop raster is incomplete.", nameof(pixels));
        cancellation.ThrowIfCancellationRequested();
        var horizontal = Kernel(sigmaX); var vertical = Kernel(sigmaY);
        int radiusX = horizontal.Length - 1, radiusY = vertical.Length - 1;
        int paddedRow = checked((width + 2 * radiusX) * 4);
        float[] input = ArrayPool<float>.Shared.Rent(checked(paddedRow * height));
        float[]? middle = null;
        try
        {
            middle = ArrayPool<float>.Shared.Rent(checked(row * (height + 2 * radiusY)));
            byte[] output = new byte[length];
            var xWeights = horizontal.Select(value => new Vector<float>(value)).ToArray();
            var yWeights = vertical.Select(value => new Vector<float>(value)).ToArray();
            int vectorWidth = Vector<float>.Count;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4), CancellationToken = cancellation };
            Parallel.For(0, height, options, y =>
            {
                int source = y * row, line = y * paddedRow, center = line + radiusX * 4, target = (y + radiusY) * row;
                for (int x = 0; x < row; x++) input[center + x] = pixels[source + x];
                // The captured raster already includes the entire three-sigma
                // support and overscan. Edge extension therefore never affects
                // the visible filter viewport, including its entrance movement.
                for (int x = 0; x < radiusX; x++)
                    for (int channel = 0; channel < 4; channel++)
                    {
                        input[line + x * 4 + channel] = pixels[source + channel];
                        input[line + (width + radiusX + x) * 4 + channel] = pixels[source + row - 4 + channel];
                    }
                int index = 0;
                for (; index <= row - vectorWidth; index += vectorWidth)
                {
                    var sum = new Vector<float>(input, center + index) * xWeights[0];
                    for (int offset = 1; offset <= radiusX; offset++)
                        sum += (new Vector<float>(input, center + index - offset * 4) + new Vector<float>(input, center + index + offset * 4)) * xWeights[offset];
                    sum.CopyTo(middle, target + index);
                }
                for (; index < row; index++)
                {
                    float sum = input[center + index] * horizontal[0];
                    for (int offset = 1; offset <= radiusX; offset++) sum += (input[center + index - offset * 4] + input[center + index + offset * 4]) * horizontal[offset];
                    middle[target + index] = sum;
                }
            });
            for (int offset = 0; offset < radiusY; offset++)
            {
                Array.Copy(middle, radiusY * row, middle, offset * row, row);
                Array.Copy(middle, (radiusY + height - 1) * row, middle, (radiusY + height + offset) * row, row);
            }
            Parallel.For(0, height, options, () => new float[row], (y, _, scratch) =>
            {
                int center = (y + radiusY) * row, target = y * row, index = 0;
                for (; index <= row - vectorWidth; index += vectorWidth)
                {
                    var sum = new Vector<float>(middle, center + index) * yWeights[0];
                    for (int offset = 1; offset <= radiusY; offset++)
                        sum += (new Vector<float>(middle, center + index - offset * row) + new Vector<float>(middle, center + index + offset * row)) * yWeights[offset];
                    sum.CopyTo(scratch, index);
                }
                for (; index < row; index++)
                {
                    float sum = middle[center + index] * vertical[0];
                    for (int offset = 1; offset <= radiusY; offset++) sum += (middle[center + index - offset * row] + middle[center + index + offset * row]) * vertical[offset];
                    scratch[index] = sum;
                }
                for (int x = 0; x < row; x += 4)
                {
                    float blue = scratch[x], green = scratch[x + 1], red = scratch[x + 2], alpha = Math.Clamp(scratch[x + 3], 0, 255);
                    float luminance = red * .213f + green * .715f + blue * .072f;
                    // CSS saturate() operates in sRGB. Preserve premultiplication
                    // and clamp each color to alpha, including translucent input.
                    output[target + x] = Channel(luminance + (blue - luminance) * (float)saturation, alpha);
                    output[target + x + 1] = Channel(luminance + (green - luminance) * (float)saturation, alpha);
                    output[target + x + 2] = Channel(luminance + (red - luminance) * (float)saturation, alpha);
                    output[target + x + 3] = (byte)MathF.Round(alpha);
                }
                return scratch;
            }, _ => { });
            cancellation.ThrowIfCancellationRequested();
            return output;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(input);
            if (middle is not null) ArrayPool<float>.Shared.Return(middle);
        }
    }

    private static byte Channel(float value, float alpha) => (byte)Math.Clamp(MathF.Round(value), 0, alpha);
    private static float[] Kernel(double sigma)
    {
        int radius = (int)Math.Ceiling(3 * sigma);
        var weights = new float[radius + 1]; weights[0] = 1;
        double sum = 1;
        for (int offset = 1; offset <= radius; offset++) { weights[offset] = (float)Math.Exp(-.5 * offset * offset / (sigma * sigma)); sum += 2 * weights[offset]; }
        for (int offset = 0; offset <= radius; offset++) weights[offset] /= (float)sum;
        return weights;
    }
}
