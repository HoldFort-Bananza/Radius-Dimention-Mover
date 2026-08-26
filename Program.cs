using System;
using System.Windows.Forms;

namespace RadiusDimensionMover
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--testrun")
            {
                var service = new RadiusDimensionService();
                var result = service.AutoPlaceWithCollisionAvoidance(Console.WriteLine);
                Console.WriteLine($"MovedCount={result.MovedCount}, TotalCount={result.TotalCount}");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
