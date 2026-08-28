namespace DemoViewer.NET.Playback2D.Pipeline.Goldens;

/// <summary>
///     Structural similarity over a luma plane — the metric that makes the cross-backend policy mean
///     something (plans/C2-gpu-provider.md §7.3).
///     <para>
///         <b>Why it is here at all.</b> A per-channel tolerance passes an image translated by one pixel:
///         every pixel is close to <i>a</i> pixel, so nothing exceeds the budget, and a whole scene can
///         slide sideways unnoticed. SSIM sees that immediately, because it compares local structure
///         rather than local values.
///     </para>
///     <para>
///         <b>Why it is written out rather than pulled in.</b> Adding an imaging library to compare two
///         bitmaps SkiaSharp already gives pixel access to would be unjustified weight, and Pipeline's
///         package set is deliberately small. Wang et al. 2004's parameters verbatim: an 11×11 Gaussian
///         window with σ = 1.5, C₁ = (0.01·255)², C₂ = (0.03·255)².
///     </para>
/// </summary>
internal static class Ssim
{
    private const int WindowSize = 11;
    private const double Sigma = 1.5;
    private const double C1 = 0.01 * 255 * (0.01 * 255);
    private const double C2 = 0.03 * 255 * (0.03 * 255);

    /// <summary>
    ///     Computes the mean and the worst windowed SSIM between two equally-sized luma planes.
    /// </summary>
    /// <param name="x">The expected image's luma, row-major.</param>
    /// <param name="y">The actual image's luma, row-major.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="mean">Receives the mean SSIM over all windows.</param>
    /// <param name="worst">Receives the single lowest window SSIM.</param>
    public static void Compute(float[] x, float[] y, int width, int height, out double mean,
        out double worst)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        // A window wider than the image has no valid positions, so shrink it (kept odd) rather than
        // reporting a perfect score for every thumbnail-sized comparison.
        int window = Math.Min(WindowSize, Math.Min(width, height));
        if (window % 2 == 0)
        {
            window--;
        }

        if (window < 1 || width <= 0 || height <= 0)
        {
            mean = 1.0;
            worst = 1.0;
            return;
        }

        double[] kernel = GaussianKernel(window, Sigma);
        int radius = window / 2;

        // Separable convolution: one horizontal pass over five statistics, then a vertical pass that
        // consumes them. The direct form is O(w·h·k²) and takes seconds at 1080p; this is O(w·h·k).
        int count = width * height;
        double[] hx = new double[count];
        double[] hy = new double[count];
        double[] hxx = new double[count];
        double[] hyy = new double[count];
        double[] hxy = new double[count];

        for (int row = 0; row < height; row++)
        {
            int offset = row * width;
            for (int centre = radius; centre < width - radius; centre++)
            {
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                for (int k = 0; k < window; k++)
                {
                    double weight = kernel[k];
                    int index = offset + centre - radius + k;
                    double xv = x[index];
                    double yv = y[index];
                    sx += weight * xv;
                    sy += weight * yv;
                    sxx += weight * xv * xv;
                    syy += weight * yv * yv;
                    sxy += weight * xv * yv;
                }

                int target = offset + centre;
                hx[target] = sx;
                hy[target] = sy;
                hxx[target] = sxx;
                hyy[target] = syy;
                hxy[target] = sxy;
            }
        }

        double sum = 0;
        long windows = 0;
        double lowest = 1.0;

        for (int centreRow = radius; centreRow < height - radius; centreRow++)
        {
            for (int column = radius; column < width - radius; column++)
            {
                double mx = 0, my = 0, mxx = 0, myy = 0, mxy = 0;
                for (int k = 0; k < window; k++)
                {
                    double weight = kernel[k];
                    int index = (centreRow - radius + k) * width + column;
                    mx += weight * hx[index];
                    my += weight * hy[index];
                    mxx += weight * hxx[index];
                    myy += weight * hyy[index];
                    mxy += weight * hxy[index];
                }

                double varianceX = mxx - mx * mx;
                double varianceY = myy - my * my;
                double covariance = mxy - mx * my;

                double numerator = (2 * mx * my + C1) * (2 * covariance + C2);
                double denominator = (mx * mx + my * my + C1) * (varianceX + varianceY + C2);
                double value = denominator == 0 ? 1.0 : numerator / denominator;

                // Floating-point noise can push an identical window a hair past 1; clamping keeps the
                // reported mean honest rather than flattering.
                value = Math.Clamp(value, -1.0, 1.0);

                sum += value;
                windows++;
                lowest = Math.Min(lowest, value);
            }
        }

        if (windows == 0)
        {
            mean = 1.0;
            worst = 1.0;
            return;
        }

        mean = sum / windows;
        worst = lowest;
    }

    private static double[] GaussianKernel(int size, double sigma)
    {
        double[] kernel = new double[size];
        int radius = size / 2;
        double total = 0;

        for (int i = 0; i < size; i++)
        {
            double offset = i - radius;
            kernel[i] = Math.Exp(-(offset * offset) / (2 * sigma * sigma));
            total += kernel[i];
        }

        for (int i = 0; i < size; i++)
        {
            kernel[i] /= total;
        }

        return kernel;
    }
}
