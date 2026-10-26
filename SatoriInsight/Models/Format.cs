namespace SatoriInsight.Models;

public static class Format
{
    public static string Bytes(long bytes) => Bytes((double)bytes);

    public static string Bytes(double bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.00} GiB" : bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.0} MiB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
}
