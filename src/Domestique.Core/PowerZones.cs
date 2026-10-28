namespace Domestique.Core;

// Coggan power zones as shown by Zwift: Z1 < 55 %, Z2 < 76 %, Z3 < 91 %, Z4 < 106 %, Z5 < 121 % of FTP, Z6 above.
public static class PowerZones
{
    private static readonly double[] Limits = [0.55, 0.76, 0.91, 1.06, 1.21];

    // 0 when not pedaling or without FTP, otherwise 1 to 6.
    public static int Of(int powerW, int ftpW)
    {
        if (powerW <= 0 || ftpW <= 0) return 0;
        double share = (double)powerW / ftpW;
        int zone = 1;
        while (zone <= Limits.Length && share >= Limits[zone - 1]) zone++;
        return zone;
    }
}
