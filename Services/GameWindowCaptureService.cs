using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcrBattleAssistant.Services;

/// <summary>
/// 捕获 PrincessConnectReDive.exe 游戏窗口客户区图像到内存（纯 Win32 GDI / DWM 内存捕获，不落地磁盘文件）
/// </summary>
public static partial class GameWindowCaptureService
{
    public const string TargetProcessName = "PrincessConnectReDive";

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(
        IntPtr hdcDest,
        int nXDest,
        int nYDest,
        int nWidth,
        int nHeight,
        IntPtr hdcSrc,
        int nXSrc,
        int nYSrc,
        uint dwRop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    private const int SW_RESTORE = 9;
    private const uint PW_CLIENTONLY = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// 查找 PrincessConnectReDive.exe 窗口并将客户区截图保存在内存中返回 BitmapSource
    /// </summary>
    public static (BitmapSource? Image, string ErrorKey, object[] ErrorArgs) CaptureGameWindowToMemory()
    {
        Process[] processes = Process.GetProcessesByName(TargetProcessName);
        if (processes.Length == 0)
        {
            Process[] allProcesses = Process.GetProcesses();
            var matched = new List<Process>();
            foreach (Process proc in allProcesses)
            {
                try
                {
                    if (proc.ProcessName.Contains("PrincessConnect", StringComparison.OrdinalIgnoreCase))
                    {
                        matched.Add(proc);
                    }
                    else
                    {
                        proc.Dispose();
                    }
                }
                catch
                {
                    proc.Dispose();
                }
            }
            processes = [.. matched];
        }

        if (processes.Length == 0)
        {
            return (null, "ErrGameNotFound", [TargetProcessName]);
        }

        IntPtr hwnd = IntPtr.Zero;
        try
        {
            foreach (Process proc in processes)
            {
                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        hwnd = proc.MainWindowHandle;
                        break;
                    }
                }
                catch
                {
                }
            }
        }
        finally
        {
            foreach (Process proc in processes)
            {
                proc.Dispose();
            }
        }

        if (hwnd == IntPtr.Zero)
        {
            return (null, "ErrWindowHandleNotFound", [TargetProcessName]);
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
            Thread.Sleep(180);
        }

        if (!GetClientRect(hwnd, out RECT clientRect))
        {
            return (null, "ErrClientRectFailed", []);
        }

        int width = clientRect.Right - clientRect.Left;
        int height = clientRect.Bottom - clientRect.Top;
        if (width < 200 || height < 120)
        {
            return (null, "ErrClientSizeInvalid", [width, height]);
        }

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        try
        {
            // 1. 优先通过 PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT) 捕获 DirectX/Unity 窗口客户区
            bool ok = PrintWindow(hwnd, hdcMem, PW_CLIENTONLY | PW_RENDERFULLCONTENT);

            BitmapSource? bitmapSource = CreateFrozenBitmapSource(hBitmap);

            // 2. 若 PrintWindow 未成功或画面全黑，回退至屏幕客户区坐标 BitBlt
            if (!ok || bitmapSource is null || IsBitmapSourceMostlyBlack(bitmapSource))
            {
                POINT pt = new() { X = 0, Y = 0 };
                ClientToScreen(hwnd, ref pt);
                BitBlt(hdcMem, 0, 0, width, height, hdcScreen, pt.X, pt.Y, SRCCOPY);
                bitmapSource = CreateFrozenBitmapSource(hBitmap);
            }

            return bitmapSource is not null
                ? (bitmapSource, string.Empty, [])
                : (null, "ErrCaptureFailed", []);
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private static BitmapSource? CreateFrozenBitmapSource(IntPtr hBitmap)
    {
        BitmapSource raw = Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

        var formatted = new FormatConvertedBitmap(raw, PixelFormats.Bgra32, null, 0);
        formatted.Freeze();
        return formatted;
    }

    private static bool IsBitmapSourceMostlyBlack(BitmapSource bmp)
    {
        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        if (w <= 0 || h <= 0)
        {
            return true;
        }

        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        int sampleCount = 0;
        int nonBlack = 0;
        int stepX = Math.Max(4, w / 16);
        int stepY = Math.Max(4, h / 10);

        for (int y = stepY; y < h - stepY; y += stepY)
        {
            int rowOffset = y * stride;
            for (int x = stepX; x < w - stepX; x += stepX)
            {
                int idx = rowOffset + x * 4;
                byte b = pixels[idx];
                byte g = pixels[idx + 1];
                byte r = pixels[idx + 2];
                sampleCount++;
                if (r > 10 || g > 10 || b > 10)
                {
                    nonBlack++;
                }
            }
        }

        return sampleCount > 0 && nonBlack < sampleCount / 10;
    }
}
