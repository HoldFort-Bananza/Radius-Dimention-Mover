using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RadiusDimensionMover
{
    /// <summary>
    /// Przechwytywanie okna Tekli przez PrintWindow (PW_RENDERFULLCONTENT) -
    /// sprawdzone empirycznie jako odporne na zasłonięcia innymi oknami
    /// (w przeciwieństwie do Graphics.CopyFromScreen, który łapie to, co
    /// faktycznie widać na ekranie w danym miejscu - i myli się, gdy coś
    /// stoi na wierzchu).
    /// </summary>
    internal static class WindowCapture
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private const uint PW_RENDERFULLCONTENT = 2;

        public static IntPtr FindTeklaWindow()
        {
            var processes = Process.GetProcessesByName("TeklaStructures");
            foreach (var p in processes)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    return p.MainWindowHandle;
                }
            }
            return IntPtr.Zero;
        }

        public static Bitmap CaptureWindow(IntPtr hWnd)
        {
            GetWindowRect(hWnd, out RECT rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
            return bmp;
        }

        /// <summary>
        /// Środek ciężkości (centroid) i liczba pikseli różniących się między
        /// dwoma zrzutami o więcej niż threshold (suma |ΔR|+|ΔG|+|ΔB|).
        /// </summary>
        public static (double cx, double cy, int count) DiffCentroid(Bitmap before, Bitmap after, int threshold = 25)
        {
            int w = Math.Min(before.Width, after.Width);
            int h = Math.Min(before.Height, after.Height);
            var rect = new Rectangle(0, 0, w, h);

            BitmapData d1 = before.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData d2 = after.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int stride1 = d1.Stride;
            int stride2 = d2.Stride;
            byte[] bytes1 = new byte[stride1 * h];
            byte[] bytes2 = new byte[stride2 * h];
            Marshal.Copy(d1.Scan0, bytes1, 0, bytes1.Length);
            Marshal.Copy(d2.Scan0, bytes2, 0, bytes2.Length);
            before.UnlockBits(d1);
            after.UnlockBits(d2);

            long sumX = 0, sumY = 0;
            int count = 0;

            for (int y = 0; y < h; y++)
            {
                int row1 = y * stride1;
                int row2 = y * stride2;
                for (int x = 0; x < w; x++)
                {
                    int i1 = row1 + x * 4;
                    int i2 = row2 + x * 4;
                    int db = Math.Abs(bytes1[i1] - bytes2[i2]);
                    int dg = Math.Abs(bytes1[i1 + 1] - bytes2[i2 + 1]);
                    int dr = Math.Abs(bytes1[i1 + 2] - bytes2[i2 + 2]);
                    if (db + dg + dr > threshold)
                    {
                        sumX += x;
                        sumY += y;
                        count++;
                    }
                }
            }

            double cx = count > 0 ? (double)sumX / count : 0;
            double cy = count > 0 ? (double)sumY / count : 0;
            return (cx, cy, count);
        }

        /// <summary>
        /// Rozpoznaje kolor RAMKI/PROWADNICY Tekli (pomarańczowa krawędź
        /// arkusza ~RGB(254,101,0), zielona linia widoku/siatki
        /// ~RGB(0,159,0)) - odróżnia je od realnej treści rysunku (biały
        /// kontur, czerwony tekst wymiaru, niebieskie/turkusowe linie
        /// opisów), żeby nie były traktowane jak "coś do ominięcia/
        /// przeskoczenia" przy szukaniu wolnego miejsca. Potwierdzone
        /// empirycznie próbkowaniem pikseli z żywego zrzutu ekranu.
        /// </summary>
        private static bool IsFrameOrGuideColor(int r, int g, int b)
        {
            bool isOrangeSheetBorder = r > 200 && g > 60 && g < 150 && b < 50;
            bool isGreenGuideLine = g > 100 && r < 50 && b < 50;
            return isOrangeSheetBorder || isGreenGuideLine;
        }

        /// <summary>
        /// Sprawdza, jaki procent pikseli w kwadracie o boku size wokół (cx,cy)
        /// jest "czymś narysowanym" (odróżnia się od czarnego tła Tekli) -
        /// używane jako prosty wykrywacz "czy tu jest już coś innego".
        /// Piksele ramki/prowadnicy (patrz IsFrameOrGuideColor) NIE liczą
        /// się jako zajętość - to nie treść rysunku do ominięcia.
        /// </summary>
        public static double GetOccupancyFraction(Bitmap bmp, double cx, double cy, int size, int backgroundThreshold = 30)
        {
            int half = size / 2;
            int startX = Math.Max(0, (int)cx - half);
            int startY = Math.Max(0, (int)cy - half);
            int endX = Math.Min(bmp.Width - 1, (int)cx + half);
            int endY = Math.Min(bmp.Height - 1, (int)cy + half);

            if (startX >= endX || startY >= endY)
            {
                return 1.0; // poza obrazem - traktuj jako zajęte/niepewne
            }

            var rect = new Rectangle(startX, startY, endX - startX, endY - startY);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int h = rect.Height;
            byte[] bytes = new byte[stride * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(data);

            int total = 0;
            int occupied = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < rect.Width; x++)
                {
                    int i = row + x * 4;
                    int b = bytes[i];
                    int g = bytes[i + 1];
                    int r = bytes[i + 2];
                    total++;
                    if ((b > backgroundThreshold || g > backgroundThreshold || r > backgroundThreshold)
                        && !IsFrameOrGuideColor(r, g, b))
                    {
                        occupied++;
                    }
                }
            }

            return total > 0 ? (double)occupied / total : 1.0;
        }

        /// <summary>
        /// Sprawdza, czy w kwadracie o boku size wokół (cx,cy) występuje
        /// kolor ramki/prowadnicy Tekli (patrz IsFrameOrGuideColor) - np.
        /// pomarańczowa krawędź arkusza. Używane jako TWARDY limit "dalej
        /// już nie" przy szukaniu miejsca dla wymiaru - w przeciwieństwie do
        /// zwykłej zajętości, tego nigdy nie próbujemy "przeskoczyć".
        /// </summary>
        public static bool HasFrameOrGuideColor(Bitmap bmp, double cx, double cy, int size)
        {
            int half = size / 2;
            int startX = Math.Max(0, (int)cx - half);
            int startY = Math.Max(0, (int)cy - half);
            int endX = Math.Min(bmp.Width - 1, (int)cx + half);
            int endY = Math.Min(bmp.Height - 1, (int)cy + half);

            if (startX >= endX || startY >= endY)
            {
                return false;
            }

            var rect = new Rectangle(startX, startY, endX - startX, endY - startY);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int h = rect.Height;
            byte[] bytes = new byte[stride * h];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(data);

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < rect.Width; x++)
                {
                    int i = row + x * 4;
                    int b = bytes[i];
                    int g = bytes[i + 1];
                    int r = bytes[i + 2];
                    if (IsFrameOrGuideColor(r, g, b))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
