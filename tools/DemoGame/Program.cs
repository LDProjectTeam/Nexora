using System;
using System.Threading;

namespace DemoGame
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "Cyber Runner 2088 — Playing";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================");
            Console.WriteLine("         CYBER RUNNER 2088 — TEST GAME            ");
            Console.WriteLine("==================================================");
            Console.ResetColor();
            Console.WriteLine("Game successfully launched via Vellum!");
            Console.WriteLine($"Arguments received: {string.Join(" ", args)}");
            Console.WriteLine($"Running directory: {AppDomain.CurrentDomain.BaseDirectory}");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Game is running... Press ENTER or ESC to exit.");
            Console.ResetColor();

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Escape)
                        break;
                }
                Thread.Sleep(100);
            }

            Console.WriteLine("Exiting game. Thanks for playing!");
        }
    }
}
