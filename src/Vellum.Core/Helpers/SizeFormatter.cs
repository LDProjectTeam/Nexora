using System;

namespace Vellum.Core.Helpers
{
    public static class SizeFormatter
    {
        public static string Format(long bytes) => PathHelper.FormatBytes(bytes);

        public static string FormatSpeed(long bytesPerSec)
            => $"{PathHelper.FormatBytes(bytesPerSec)}/s";
    }
}
