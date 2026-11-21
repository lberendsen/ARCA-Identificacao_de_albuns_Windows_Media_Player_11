using System;
using System.Globalization;

namespace ARCA.Modelo
{
    public static class Formato
    {
        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        public static string Inteiro(double v) { return v.ToString("N0", Br); }

        public static string Num(double v, int casas) { return v.ToString("N" + casas, Br); }

        public static string Tempo(double segundos)
        {
            if (segundos < 0 || double.IsNaN(segundos)) segundos = 0;
            TimeSpan t = TimeSpan.FromSeconds(segundos);
            return t.TotalHours >= 1
                ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                : t.Minutes + ":" + t.Seconds.ToString("00");
        }

        public static string Itens(int n)
        {
            return Inteiro(n) + (n == 1 ? " item" : " itens");
        }
    }
}
