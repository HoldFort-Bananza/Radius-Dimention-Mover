using System;
using System.Windows.Forms;

namespace RadiusDimensionMover
{
    /// <summary>
    /// Punkt wejścia - i nic więcej. Cała logika siedzi w
    /// RadiusDimensionService, całe UI w MainForm.
    ///
    /// UWAGA dla przyszłych zmian: przy diagnozowaniu wygodnie jest dodać tu
    /// tymczasowe przełączniki wiersza poleceń (np. --testrun uruchamiający
    /// serwis bez UI, albo --inspect wypisujący dane rysunku). Takie
    /// rusztowanie należy USUNĄĆ przed commitem - wydawany program ma mieć
    /// tylko to, co poniżej. Wzorce tych przełączników są opisane w Wiki:
    /// https://github.com/HoldFort-Bananza/Radius-Dimention-Mover/wiki/7-Diagnostyka
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
